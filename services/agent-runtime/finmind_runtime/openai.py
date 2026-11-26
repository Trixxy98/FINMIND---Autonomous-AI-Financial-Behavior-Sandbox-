"""OpenAI provider adapter (ADR-0019): the Responses API over standard-library HTTP. No SDK, no new dependency.

Built from OpenAI's Responses API reference and function-calling and reasoning guides, read on 26 November 2026,
and tested only against a fake transport. It has not been verified against the live API.

Rules this adapter keeps (the same as the Claude adapter, ADR-0012 and ADR-0013):
- Stateless and unstored: every request sends the whole conversation and `store: false`, so nothing is kept on
  OpenAI's side and FINMIND's record is the only history. No `previous_response_id`.
- The conversation is append-only: each response's output items (reasoning, messages, function calls) are appended
  unchanged, then one `function_call_output` per call. History is never edited.
- No automatic model fallback: a refusal or a content filter ends the run and is recorded.
- Tools are not strict: malformed arguments are research observations, validated by the gateway.
- Provider default sampling: no temperature or top_p is sent (ADR-0019 decision 2).
- Every request is reserved with the server first, using a byte bound on the request (no counting endpoint is used).
  Only a request that certainly did not run (a rate limit) is sent again under the same reservation.
- The key is read from OPENAI_API_KEY in the worker's environment and sent only in the Authorization header.
  Errors keep the HTTP status or the exception class name, never a body or message.
"""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, List, Optional, Tuple

from .agents import Agent, Decision, Observation
from .budget import conservative_input_tokens
from .claude import CONTINUATION, ModelApiError, WallTimeReached, _unrepresentable, load_system_prompt

API_URL = "https://api.openai.com/v1/responses"  # fixed: a configuration change cannot send the key elsewhere
ADAPTER_VERSION = "openai-responses/v1"
REQUEST_TIMEOUT_SECONDS = 600.0
RETRY_DELAYS = (1.0, 4.0)
NOT_RUN_STATUSES = {400, 401, 402, 403, 404, 409, 413, 422, 429}
RETRY_SAME_RESERVATION = {429}

Transport = Callable[[str, str, Dict[str, str], bytes, float], Tuple[int, bytes]]


class MissingCredential(Exception):
    """OPENAI_API_KEY is not set in the worker's environment."""


def https_transport(method: str, url: str, headers: Dict[str, str], body: bytes, timeout: float) -> Tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        with error:
            return error.code, error.read()


def to_openai_tools(tools: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """finmind-tools/v1 schemas to Responses API function tools, in the order received (openai-tools/v1)."""
    return [{"type": "function", "name": tool["name"], "description": tool["description"],
             "parameters": tool["parameters"], "strict": False} for tool in tools]


def _arguments(raw: Any) -> Any:
    """Parsed JSON when it parses; otherwise the raw text, which the gateway records as a malformed attempt."""
    if not isinstance(raw, str):
        return raw
    try:
        return json.loads(raw)
    except ValueError:
        return raw


class OpenAIAgent(Agent):
    MAX_OUTPUT_ATTEMPTS = 2  # one retry with unchanged history

    def __init__(self, snapshot: Dict[str, Any], tools: List[Dict[str, Any]], transport: Transport = https_transport,
                 clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep) -> None:
        key = os.environ.get("OPENAI_API_KEY", "")
        if not key:
            raise MissingCredential()
        self._key = key
        agent = snapshot["agent"]
        self._model = agent["modelId"]
        self._effort = agent["settings"]["effort"]
        self._max_tokens = int(agent["settings"].get("maxOutputTokensPerTurn", 16_000))
        self._instructions = load_system_prompt(snapshot)
        self._tools = to_openai_tools(tools)
        self._input: List[Dict[str, Any]] = [{
            "role": "user",
            "content": f"Your objective:\n{snapshot['objective']}\n\nThe simulation has started. Use the tools to observe and act.",
        }]
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
            self._append_results(observation.results)
        elif observation.turn > 1:
            self._input.append({"role": "user", "content": CONTINUATION})
        if self._reserve is None:
            raise RuntimeError("no model request is sent without a budget")

        totals = {"input": 0, "output": 0, "cache_read": 0, "latency": 0, "settled": 0}
        reply: Dict[str, Any] = {}
        try:
            for attempt in range(1, self.MAX_OUTPUT_ATTEMPTS + 1):
                self._remaining()  # do not reserve for a request there is no time to send
                bound = conservative_input_tokens(self._request(None))
                granted = self._reserve(observation.turn, attempt, bound, self._max_tokens)
                started = self._clock()
                reply = self._create(granted)
                totals["latency"] += int((self._clock() - started) * 1000)
                usage = reply.get("usage") or {}
                cached = int((usage.get("input_tokens_details") or {}).get("cached_tokens") or 0)
                totals["input"] += int(usage.get("input_tokens") or 0) - cached
                totals["cache_read"] += cached
                totals["output"] += int(usage.get("output_tokens") or 0)  # reasoning tokens are billed as output
                totals["settled"] = attempt
                if not self._out_of_tokens(reply):
                    break
        except Exception as error:
            if totals["settled"]:
                error.usage = dict(totals, cache_creation=0)
            elif isinstance(error, ModelApiError) and error.not_run:
                error.usage = dict(totals, cache_creation=0, settled=1)
            raise

        common = dict(input_tokens=totals["input"], output_tokens=totals["output"], latency_ms=totals["latency"],
                      cache_read_tokens=totals["cache_read"], cache_creation_tokens=0,
                      model=reply.get("model") or self._model, stop_reason=reply.get("status"))
        if self._out_of_tokens(reply):
            return Decision(calls=[], terminal="max_tokens", output_text="", **common)
        reason = (reply.get("incomplete_details") or {}).get("reason")
        if reason == "content_filter":
            return Decision(calls=[], terminal="refusal", refusal_category="content_filter", output_text="", **common)

        output = list(reply.get("output") or [])
        parts = [part for item in output if item.get("type") == "message" for part in item.get("content") or []]
        if any(part.get("type") == "refusal" for part in parts):
            return Decision(calls=[], terminal="refusal", refusal_category="refusal", output_text="", **common)

        self._input.extend(output)  # unchanged, reasoning items included
        calls = [item for item in output if item.get("type") == "function_call"]
        self._pending = [item["call_id"] for item in calls]
        text = "\n".join(part.get("text", "") for part in parts if part.get("type") == "output_text")
        parsed = [(item["name"], _arguments(item.get("arguments"))) for item in calls]
        terminal = "unrepresentable" if _unrepresentable(parsed) else None
        return Decision(calls=parsed, output_text=text, terminal=terminal, **common)

    @staticmethod
    def _out_of_tokens(reply: Dict[str, Any]) -> bool:
        return reply.get("status") == "incomplete" and (reply.get("incomplete_details") or {}).get("reason") == "max_output_tokens"

    def _append_results(self, results: List[Any]) -> None:
        if len(results) != len(self._pending):
            raise RuntimeError("every function call needs exactly one output")
        for call_id, (_, response) in zip(self._pending, results):
            self._input.append({"type": "function_call_output", "call_id": call_id,
                                "output": json.dumps(response, sort_keys=True)})
        self._pending = []

    def _request(self, max_output_tokens: Optional[int]) -> Dict[str, Any]:
        body: Dict[str, Any] = {"model": self._model, "instructions": self._instructions, "input": self._input,
                                "tools": self._tools, "tool_choice": "auto", "parallel_tool_calls": True,
                                "reasoning": {"effort": self._effort}, "store": False}
        if max_output_tokens is not None:
            body["max_output_tokens"] = max_output_tokens
        return body

    def _remaining(self) -> float:
        remaining = self._deadline - self._clock()
        if remaining < 1:
            raise WallTimeReached()
        return remaining

    def _create(self, max_output_tokens: int) -> Dict[str, Any]:
        body = json.dumps(self._request(max_output_tokens), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {"Authorization": f"Bearer {self._key}", "Content-Type": "application/json"}
        for delay in (*RETRY_DELAYS, None):
            timeout = min(REQUEST_TIMEOUT_SECONDS, self._remaining())
            try:
                status, raw = self._transport("POST", API_URL, headers, body, timeout)
            except Exception as error:  # a timeout or a dropped connection: the request may have run
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
        raise ModelApiError("HTTP 429")  # not reached: the last attempt raises above
