"""Gemini provider adapter (ADR-0019): Google's Interactions API with store=false, over standard-library HTTP. No SDK.

Built from Google's Interactions API reference and its function-calling, thinking, and API errors pages
(aistudio.google.com), read on 26 November 2026, and tested only against a fake transport. It has not been verified
against the live API. Google calls generateContent legacy and recommends Interactions for new projects.

Rules this adapter keeps:
- Stateless and unstored: `store: false`, the whole history sent each turn, no `previous_interaction_id`, no
  background execution. Interactions are otherwise kept for 55 days (paid) or 1 day (free) by default.
- Append-only: every returned step (thought steps with their signatures, function calls, model output) is appended
  unchanged, as Google requires ("You MUST always resend all thought blocks exactly as they were received"), then one
  `function_result` per call, in the verbatim documented shape.
- No temperature or top_p (ignored or rejected by newer Gemini models; provider defaults). Tools are not strict.
- Every request is reserved first with the byte bound. Only a 429 is retried, under the same reservation. Blocked or
  malformed generations end the run with Google's error code and keep their reservation (billing is not stated).
- Usage: billed output is output plus thinking tokens. Whether cached tokens are counted inside total_input_tokens is
  not stated, so input is not reduced by them; tool-use prompt tokens are counted as input. The recorded cost can only
  be too high, never too low.
- The key is read from GEMINI_API_KEY and sent only in the x-goog-api-key header, never in the URL.
"""

from __future__ import annotations

import json
import os
import re
import time
from typing import Any, Callable, Dict, List, Optional

from .agents import Agent, Decision, Observation
from .budget import conservative_input_tokens
from .claude import CONTINUATION, ModelApiError, WallTimeReached, _unrepresentable, load_system_prompt
from .openai import Transport, _arguments, https_transport

API_URL = "https://generativelanguage.googleapis.com/v1beta/interactions"  # fixed: the key cannot be sent elsewhere
ADAPTER_VERSION = "gemini-interactions/v1"
REQUEST_TIMEOUT_SECONDS = 600.0
RETRY_DELAYS = (1.0, 4.0)
RETRY_SAME_RESERVATION = {429}
NOT_RUN_STATUSES = {400, 401, 402, 403, 404}
# Generation outcomes (API errors page): the model ran, so the request may have been billed.
GENERATED = {"safety", "recitation", "language", "prohibited_content", "spii", "blocklist", "content_blocked",
             "malformed_function_call", "malformed_tool_call", "unexpected_tool_call", "too_many_tool_calls",
             "missing_thought_signature"}
NORMAL_STATUSES = {"completed", "requires_action"}
CODE = re.compile(r"^[a-z_]{1,40}$")


class MissingCredential(Exception):
    """GEMINI_API_KEY is not set in the worker's environment."""


def to_gemini_tools(tools: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """finmind-tools/v1 schemas to Interactions function tools, in the order received (gemini-tools/v1)."""
    return [{"type": "function", "name": t["name"], "description": t["description"], "parameters": t["parameters"]} for t in tools]


def _user(text: str) -> Dict[str, Any]:
    return {"type": "user_input", "content": [{"type": "text", "text": text}]}


class GeminiAgent(Agent):
    MAX_OUTPUT_ATTEMPTS = 2

    def __init__(self, snapshot: Dict[str, Any], tools: List[Dict[str, Any]], transport: Transport = https_transport,
                 clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep) -> None:
        key = os.environ.get("GEMINI_API_KEY", "")
        if not key:
            raise MissingCredential()
        self._key = key
        agent = snapshot["agent"]
        self._model = agent["modelId"]
        self._level = agent["settings"]["effort"]
        self._max_tokens = int(agent["settings"].get("maxOutputTokensPerTurn", 16_000))
        self._instruction = load_system_prompt(snapshot)
        self._tools = to_gemini_tools(tools)
        self._input: List[Dict[str, Any]] = [_user(
            f"Your objective:\n{snapshot['objective']}\n\nThe simulation has started. Use the tools to observe and act.")]
        self._pending: List[Dict[str, str]] = []
        self._transport = transport
        self._clock = clock
        self._sleep = sleep
        self._deadline = clock() + int(snapshot["maxWallTimeSeconds"])
        self._reserve: Optional[Callable[[int, int, int, int], int]] = None

    def use_budget(self, reserve: Callable[[int, int, int, int], int]) -> None:
        self._reserve = reserve

    def next_action(self, observation: Observation) -> Decision:
        if self._pending:
            if len(observation.results) != len(self._pending):
                raise RuntimeError("every function call needs exactly one result")
            for call, (_, response) in zip(self._pending, observation.results):
                self._input.append({"type": "function_result", "name": call["name"], "call_id": call["id"],
                                    "result": [{"type": "text", "text": json.dumps(response, sort_keys=True)}]})
            self._pending = []
        elif observation.turn > 1:
            self._input.append(_user(CONTINUATION))
        if self._reserve is None:
            raise RuntimeError("no model request is sent without a budget")

        totals = {"input": 0, "output": 0, "cache_read": 0, "latency": 0, "settled": 0}
        reply: Dict[str, Any] = {}
        try:
            for attempt in range(1, self.MAX_OUTPUT_ATTEMPTS + 1):
                self._remaining()
                granted = self._reserve(observation.turn, attempt, conservative_input_tokens(self._request(None)), self._max_tokens)
                started = self._clock()
                reply = self._create(granted)
                totals["latency"] += int((self._clock() - started) * 1000)
                usage = reply.get("usage") or {}
                totals["input"] += int(usage.get("total_input_tokens") or 0) + int(usage.get("total_tool_use_tokens") or 0)
                totals["cache_read"] += int(usage.get("total_cached_tokens") or 0)
                totals["output"] += int(usage.get("total_output_tokens") or 0) + int(usage.get("total_thought_tokens") or 0)
                totals["settled"] = attempt
                if reply.get("status") != "incomplete":
                    break
            status = reply.get("status")
            if status not in NORMAL_STATUSES and status != "incomplete":
                failure = ModelApiError(f"status {status if isinstance(status, str) and CODE.match(status) else 'unknown'}")
                raise failure
        except Exception as error:
            if totals["settled"]:
                error.usage = dict(totals, cache_creation=0)
            elif isinstance(error, ModelApiError) and error.not_run:
                error.usage = dict(totals, cache_creation=0, settled=1)
            raise

        common = dict(input_tokens=totals["input"], output_tokens=totals["output"], latency_ms=totals["latency"],
                      cache_read_tokens=totals["cache_read"], cache_creation_tokens=0,
                      model=reply.get("model") or self._model, stop_reason=reply.get("status"))
        if reply.get("status") == "incomplete":
            return Decision(calls=[], terminal="max_tokens", output_text="", **common)

        steps = list(reply.get("steps") or [])
        self._input.extend(steps)  # unchanged, thought signatures included
        calls = [step for step in steps if step.get("type") == "function_call"]
        self._pending = [{"id": step.get("id", ""), "name": step.get("name", "")} for step in calls]
        text = "\n".join(block.get("text", "") for step in steps if step.get("type") == "model_output"
                         for block in step.get("content") or [] if block.get("type") == "text")
        parsed = [(step["name"], _arguments(step.get("arguments"))) for step in calls]
        terminal = "unrepresentable" if _unrepresentable(parsed) else None
        return Decision(calls=parsed, output_text=text, terminal=terminal, **common)

    def _request(self, max_output_tokens: Optional[int]) -> Dict[str, Any]:
        config: Dict[str, Any] = {"thinking_level": self._level, "tool_choice": "auto"}
        if max_output_tokens is not None:
            config = {"max_output_tokens": max_output_tokens, **config}
        return {"model": self._model, "system_instruction": self._instruction, "input": self._input, "tools": self._tools,
                "generation_config": config, "store": False}

    def _remaining(self) -> float:
        remaining = self._deadline - self._clock()
        if remaining < 1:
            raise WallTimeReached()
        return remaining

    def _create(self, max_output_tokens: int) -> Dict[str, Any]:
        body = json.dumps(self._request(max_output_tokens), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {"x-goog-api-key": self._key, "Content-Type": "application/json"}
        for delay in (*RETRY_DELAYS, None):
            timeout = min(REQUEST_TIMEOUT_SECONDS, self._remaining())
            try:
                status, raw = self._transport("POST", API_URL, headers, body, timeout)
            except Exception as error:
                raise ModelApiError(type(error).__name__) from None
            if status == 200:
                try:
                    return json.loads(raw)
                except ValueError:
                    raise ModelApiError("InvalidResponse") from None
            if delay is not None and status in RETRY_SAME_RESERVATION:
                self._sleep(delay)
                continue
            code = self._error_code(raw)
            failure = ModelApiError(f"HTTP {status}" + (f" {code}" if code else ""))
            failure.not_run = status in NOT_RUN_STATUSES and code not in GENERATED
            raise failure from None
        raise ModelApiError("HTTP 429")

    @staticmethod
    def _error_code(raw: bytes) -> Optional[str]:
        """Only Google's snake_case code is kept: never the message, which may echo request content."""
        try:
            code = (json.loads(raw).get("error") or {}).get("code")
        except (ValueError, AttributeError):
            return None
        return code if isinstance(code, str) and CODE.match(code) else None
