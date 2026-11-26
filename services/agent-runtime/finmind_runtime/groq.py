"""Groq provider adapter (ADR-0019): OpenAI-compatible Chat Completions over standard-library HTTP. No SDK.

Built from Groq's API reference, tool-use, reasoning, models, and errors pages, read on 26 November 2026, and tested
only against a fake transport. It has not been verified against the live API.

Rules this adapter keeps (as the other adapters, with Groq's documented limits):
- Only documented request fields are sent. `store` is not supported by Groq's Chat Completions, so none is sent;
  Groq's data-retention terms are not stated on the pages read and are for each user to check.
- The conversation is append-only: each assistant turn is replayed with its documented fields (role, content,
  tool_calls), then one role "tool" message per call. The model's `reasoning` text is not sent back: Groq does not
  document it as an accepted input field, and an unsupported message field returns a 400.
- `parallel_tool_calls` is false: Groq's tool-use table marks parallel tool use as unsupported for gpt-oss.
- No temperature or top_p (provider defaults). Tools are not strict; malformed arguments reach the gateway as text.
- Every request is reserved first with the byte bound. 429 and 498 (flex capacity) are retried under the same
  reservation. Groq states server errors (500, 502, 503) are not charged; a 422's billing is not stated, so its
  reservation is kept.
- The key is read from GROQ_API_KEY in the worker's environment and sent only in the Authorization header.
"""

from __future__ import annotations

import json
import os
import time
from typing import Any, Callable, Dict, List, Optional

from .agents import Agent, Decision, Observation
from .budget import conservative_input_tokens
from .claude import CONTINUATION, ModelApiError, WallTimeReached, _unrepresentable, load_system_prompt
from .openai import Transport, _arguments, https_transport

API_URL = "https://api.groq.com/openai/v1/chat/completions"  # fixed: a configuration change cannot send the key elsewhere
ADAPTER_VERSION = "groq-chat/v1"
REQUEST_TIMEOUT_SECONDS = 600.0
RETRY_DELAYS = (1.0, 4.0)
NOT_RUN_STATUSES = {400, 401, 403, 404, 413, 429, 498, 500, 502, 503}
RETRY_SAME_RESERVATION = {429, 498}


class MissingCredential(Exception):
    """GROQ_API_KEY is not set in the worker's environment."""


def to_chat_tools(tools: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """finmind-tools/v1 schemas to Chat Completions function tools, in the order received (groq-tools/v1)."""
    return [{"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["parameters"]}}
            for t in tools]


class GroqAgent(Agent):
    MAX_OUTPUT_ATTEMPTS = 2

    def __init__(self, snapshot: Dict[str, Any], tools: List[Dict[str, Any]], transport: Transport = https_transport,
                 clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep) -> None:
        key = os.environ.get("GROQ_API_KEY", "")
        if not key:
            raise MissingCredential()
        self._key = key
        agent = snapshot["agent"]
        self._model = agent["modelId"]
        self._effort = agent["settings"]["effort"]
        self._max_tokens = int(agent["settings"].get("maxOutputTokensPerTurn", 16_000))
        self._tools = to_chat_tools(tools)
        self._messages: List[Dict[str, Any]] = [
            {"role": "system", "content": load_system_prompt(snapshot)},
            {"role": "user", "content": f"Your objective:\n{snapshot['objective']}\n\nThe simulation has started. Use the tools to observe and act."},
        ]
        self._pending: List[str] = []
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
                raise RuntimeError("every tool call needs exactly one result")
            for call_id, (_, response) in zip(self._pending, observation.results):
                self._messages.append({"role": "tool", "tool_call_id": call_id, "content": json.dumps(response, sort_keys=True)})
            self._pending = []
        elif observation.turn > 1:
            self._messages.append({"role": "user", "content": CONTINUATION})
        if self._reserve is None:
            raise RuntimeError("no model request is sent without a budget")

        totals = {"input": 0, "output": 0, "latency": 0, "settled": 0}
        reply: Dict[str, Any] = {}
        choice: Dict[str, Any] = {}
        try:
            for attempt in range(1, self.MAX_OUTPUT_ATTEMPTS + 1):
                self._remaining()
                granted = self._reserve(observation.turn, attempt, conservative_input_tokens(self._request(None)), self._max_tokens)
                started = self._clock()
                reply = self._create(granted)
                totals["latency"] += int((self._clock() - started) * 1000)
                usage = reply.get("usage") or {}
                totals["input"] += int(usage.get("prompt_tokens") or 0)
                totals["output"] += int(usage.get("completion_tokens") or 0)
                totals["settled"] = attempt
                choice = (reply.get("choices") or [{}])[0]
                if choice.get("finish_reason") != "length":
                    break
        except Exception as error:
            if totals["settled"]:
                error.usage = dict(totals, cache_read=0, cache_creation=0)
            elif isinstance(error, ModelApiError) and error.not_run:
                error.usage = dict(totals, cache_read=0, cache_creation=0, settled=1)
            raise

        finish = choice.get("finish_reason")
        common = dict(input_tokens=totals["input"], output_tokens=totals["output"], latency_ms=totals["latency"],
                      cache_read_tokens=0, cache_creation_tokens=0, model=reply.get("model") or self._model, stop_reason=finish)
        if finish == "length":
            return Decision(calls=[], terminal="max_tokens", output_text="", **common)
        if finish == "content_filter":
            return Decision(calls=[], terminal="refusal", refusal_category="content_filter", output_text="", **common)

        message = choice.get("message") or {}
        tool_calls = list(message.get("tool_calls") or [])
        replay: Dict[str, Any] = {"role": "assistant", "content": message.get("content")}
        if tool_calls:
            replay["tool_calls"] = tool_calls
        self._messages.append(replay)
        self._pending = [call["id"] for call in tool_calls]
        parsed = [(call["function"]["name"], _arguments(call["function"].get("arguments"))) for call in tool_calls]
        terminal = "unrepresentable" if _unrepresentable(parsed) else None
        return Decision(calls=parsed, output_text=message.get("content") or "", terminal=terminal, **common)

    def _request(self, max_completion_tokens: Optional[int]) -> Dict[str, Any]:
        body: Dict[str, Any] = {"model": self._model, "messages": self._messages, "tools": self._tools, "tool_choice": "auto",
                                "parallel_tool_calls": False, "reasoning_effort": self._effort}
        if max_completion_tokens is not None:
            body["max_completion_tokens"] = max_completion_tokens
        return body

    def _remaining(self) -> float:
        remaining = self._deadline - self._clock()
        if remaining < 1:
            raise WallTimeReached()
        return remaining

    def _create(self, max_completion_tokens: int) -> Dict[str, Any]:
        body = json.dumps(self._request(max_completion_tokens), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {"Authorization": f"Bearer {self._key}", "Content-Type": "application/json"}
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
            failure = ModelApiError(f"HTTP {status}")
            failure.not_run = status in NOT_RUN_STATUSES
            raise failure from None
        raise ModelApiError("HTTP 429")
