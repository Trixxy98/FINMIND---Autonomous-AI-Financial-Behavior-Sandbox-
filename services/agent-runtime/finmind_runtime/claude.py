"""Claude provider adapter (ADR-0012), using the official Anthropic Python SDK (pinned in requirements.txt).

A manual tool loop is used instead of the SDK tool runner because each turn must be recorded, executed through
the FINMIND gateway, and checked against server budgets before the next request.

Rules this adapter keeps:
- The conversation is append-only: each response's content is appended unchanged, then one user message holds
  every tool_result for that response. History is never edited (thinking blocks and caching depend on it).
- No automatic model fallback: a refusal ends the run and is recorded (research integrity, ADR-0012).
- Tools are not strict: malformed arguments are research observations, validated by the gateway.
- Every paid request is reserved with the server first (ADR-0013 A1). The SDK does not retry by itself, and a
  request whose billing is unknown is never retried: its reservation stays charged.
- The API key is read by the SDK from the worker's environment and never passed anywhere else.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from .agents import Agent, Decision, Observation

API_BASE_URL = "https://api.anthropic.com"  # set explicitly so an environment variable cannot redirect requests
PROMPT_FILE = "finmind-agent-prompt.v1.md"
UNAVAILABLE_PLACEHOLDER = "{{UNAVAILABLE_CAPABILITIES}}"
UNAVAILABLE_TEXT = ("- These capabilities do not exist in this simulation and cannot be called: "
                    "set_balance_directly, edit_ledger, delete_transaction, create_money.")
CONTINUATION = "The simulation is still running. Use the tools to act, wait, or finish."
ERROR_STATUSES = {"malformed", "unsupported_capability"}

REQUEST_TIMEOUT_SECONDS = 600.0
COUNT_TIMEOUT_SECONDS = 30.0
RETRY_DELAYS = (1.0, 4.0)
# Responses that mean the request was turned away before the model ran. Anything else may have been billed.
NOT_RUN_STATUSES = {400, 401, 402, 403, 404, 413, 422, 429, 529}
RETRY_SAME_RESERVATION = {429, 529}
# Token counts are estimates, so the reservation adds a margin.
INPUT_MARGIN_TOKENS = 1_024
INPUT_MARGIN_PERCENT = 5


class PromptMismatch(Exception):
    """The prompt file on disk is not the one recorded in the run snapshot."""


class ModelApiError(Exception):
    """The model API failed. Only the error class name is kept."""

    usage: Optional[Dict[str, int]] = None  # what the turn is known to have used before it failed
    not_run = False  # True when the failed request cannot have been billed


class WallTimeReached(Exception):
    """No wall time is left for another request."""

    usage: Optional[Dict[str, int]] = None


def find_prompts_dir() -> Path:
    configured = os.environ.get("FINMIND_PROMPTS_DIR")
    if configured:
        return Path(configured)
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "prompts"
        if (candidate / PROMPT_FILE).is_file():
            return candidate
    raise FileNotFoundError("No prompts directory found. Set FINMIND_PROMPTS_DIR.")


def load_system_prompt(snapshot: Dict[str, Any]) -> str:
    raw = (find_prompts_dir() / PROMPT_FILE).read_bytes()
    if hashlib.sha256(raw).hexdigest() != snapshot.get("promptHash"):
        raise PromptMismatch("prompt file does not match the run snapshot")
    section = UNAVAILABLE_TEXT if snapshot.get("describeUnavailableCapabilities") else ""
    return raw.decode("utf-8").replace(UNAVAILABLE_PLACEHOLDER, section).rstrip() + "\n"


def to_claude_tools(tools: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """finmind-tools/v1 schemas (claim response) to Anthropic tool definitions, in the order received."""
    return [{"name": tool["name"], "description": tool["description"], "input_schema": tool["parameters"]}
            for tool in tools]


def _unrepresentable(value: Any, depth: int = 0) -> bool:
    """True if the value cannot be sent back unchanged: a lone surrogate, or a non-finite number (ADR-0013 item 19)."""
    if depth > 200:
        return False
    if isinstance(value, str):
        return any(0xD800 <= ord(char) <= 0xDFFF for char in value)
    if isinstance(value, float):
        return not math.isfinite(value)
    if isinstance(value, dict):
        return any(_unrepresentable(k, depth + 1) or _unrepresentable(v, depth + 1) for k, v in value.items())
    if isinstance(value, (list, tuple)):
        return any(_unrepresentable(item, depth + 1) for item in value)
    if hasattr(value, "__dict__"):
        return _unrepresentable(vars(value), depth + 1)
    return False


def _usage(response: Any, name: str) -> int:
    return int(getattr(response.usage, name, None) or 0)


class ClaudeAgent(Agent):
    MAX_TOKENS_ATTEMPTS = 2  # one retry with unchanged history (ADR-0012)

    def __init__(self, snapshot: Dict[str, Any], tools: List[Dict[str, Any]], client: Any = None,
                 clock: Callable[[], float] = time.monotonic,
                 sleep: Callable[[float], None] = time.sleep) -> None:
        agent = snapshot["agent"]
        self._model = agent["modelId"]
        self._effort = agent["settings"]["effort"]
        self._max_tokens = int(agent["settings"].get("maxOutputTokensPerTurn", 16_000))
        self._system = load_system_prompt(snapshot)
        self._tools = to_claude_tools(tools)
        self._messages: List[Dict[str, Any]] = [{
            "role": "user",
            "content": f"Your objective:\n{snapshot['objective']}\n\nThe simulation has started. Use the tools to observe and act.",
        }]
        self._pending: List[str] = []
        self._client = client if client is not None else self._default_client()
        self._clock = clock
        self._sleep = sleep
        self._deadline = clock() + int(snapshot["maxWallTimeSeconds"])
        self._reserve: Optional[Callable[[int, int, int, int], int]] = None

    def use_budget(self, reserve: Callable[[int, int, int, int], int]) -> None:
        """reserve(turn, attempt, input_tokens, max_output_tokens) returns the granted output limit, or raises."""
        self._reserve = reserve

    @staticmethod
    def _default_client() -> Any:
        import anthropic  # only needed for real runs
        return anthropic.Anthropic(base_url=API_BASE_URL, max_retries=0, timeout=REQUEST_TIMEOUT_SECONDS)

    def next_action(self, observation: Observation) -> Decision:
        if self._pending:
            self._append_tool_results(observation.results)
        elif observation.turn > 1:
            self._messages.append({"role": "user", "content": CONTINUATION})

        if self._reserve is None:
            raise RuntimeError("no model request is sent without a budget")
        totals = {"input": 0, "output": 0, "cache_read": 0, "cache_creation": 0, "latency": 0, "settled": 0}
        response = None
        try:
            input_bound = self._input_bound()
            for attempt in range(1, self.MAX_TOKENS_ATTEMPTS + 1):
                self._remaining()  # do not reserve for a request there is no time to send
                granted = self._reserve(observation.turn, attempt, input_bound, self._max_tokens)
                started = self._clock()
                response = self._create(granted)
                totals["latency"] += int((self._clock() - started) * 1000)
                totals["input"] += _usage(response, "input_tokens")
                totals["output"] += _usage(response, "output_tokens")
                totals["cache_read"] += _usage(response, "cache_read_input_tokens")
                totals["cache_creation"] += _usage(response, "cache_creation_input_tokens")
                totals["settled"] = attempt
                if response.stop_reason != "max_tokens":
                    break
        except Exception as error:
            # An answered attempt was paid for, whatever happened next. An unanswered one stays reserved,
            # unless it is the only one and certainly did not run.
            if totals["settled"]:
                error.usage = dict(totals)
            elif isinstance(error, ModelApiError) and error.not_run:
                error.usage = dict(totals, settled=1)
            raise

        common = dict(input_tokens=totals["input"], output_tokens=totals["output"], latency_ms=totals["latency"],
                      cache_read_tokens=totals["cache_read"], cache_creation_tokens=totals["cache_creation"],
                      model=getattr(response, "model", self._model), stop_reason=response.stop_reason)

        if response.stop_reason == "max_tokens":
            return Decision(calls=[], terminal="max_tokens", output_text="", **common)
        if response.stop_reason == "refusal":
            category = getattr(getattr(response, "stop_details", None), "category", None)
            return Decision(calls=[], terminal="refusal", refusal_category=category, output_text="", **common)

        # Append the response unchanged (text, thinking, and tool_use blocks).
        self._messages.append({"role": "assistant", "content": response.content})
        tool_uses = [block for block in response.content if block.type == "tool_use"]
        self._pending = [block.id for block in tool_uses]
        text = "\n".join(block.text for block in response.content if block.type == "text")
        # The SDK cannot send such a response back unchanged, and history is never edited (ADR-0012), so the
        # turn's calls are still recorded and then the run ends (ADR-0013 item 19).
        terminal = "unrepresentable" if _unrepresentable(response.content) else None
        return Decision(calls=[(block.name, block.input) for block in tool_uses], output_text=text, terminal=terminal,
                        **common)

    def _append_tool_results(self, results: List[Any]) -> None:
        if len(results) != len(self._pending):
            raise RuntimeError("every tool_use needs exactly one tool_result")
        content = []
        for tool_use_id, (_, response) in zip(self._pending, results):
            block: Dict[str, Any] = {"type": "tool_result", "tool_use_id": tool_use_id,
                                     "content": json.dumps(response, sort_keys=True)}
            if isinstance(response, dict) and response.get("status") in ERROR_STATUSES:
                block["is_error"] = True
            content.append(block)
        self._messages.append({"role": "user", "content": content})
        self._pending = []

    def _request(self) -> Dict[str, Any]:
        return dict(model=self._model, system=self._system, tools=self._tools, messages=self._messages,
                    thinking={"type": "adaptive"}, output_config={"effort": self._effort},
                    cache_control={"type": "ephemeral"})

    def _remaining(self) -> float:
        remaining = self._deadline - self._clock()
        if remaining < 1:
            raise WallTimeReached()
        return remaining

    def _input_bound(self) -> int:
        """Counting is free, so any temporary failure may be retried."""
        for delay in (*RETRY_DELAYS, None):
            timeout = min(COUNT_TIMEOUT_SECONDS, self._remaining())
            try:
                counted = self._client.messages.count_tokens(**self._request(), timeout=timeout).input_tokens
                return counted + max(INPUT_MARGIN_TOKENS, counted * INPUT_MARGIN_PERCENT // 100)
            except Exception as error:
                status = getattr(error, "status_code", None)
                temporary = status is None or status in {408, 409, 429} or status >= 500
                if delay is None or not temporary:
                    raise ModelApiError(type(error).__name__) from None
                self._sleep(delay)

    def _create(self, max_tokens: int) -> Any:
        """One reserved request. Only a request that did not run is sent again, under the same reservation."""
        for delay in (*RETRY_DELAYS, None):
            timeout = min(REQUEST_TIMEOUT_SECONDS, self._remaining())
            try:
                return self._client.messages.create(**self._request(), max_tokens=max_tokens, timeout=timeout)
            except Exception as error:
                status = getattr(error, "status_code", None)
                if delay is not None and status in RETRY_SAME_RESERVATION:
                    self._sleep(delay)
                    continue
                failure = ModelApiError(type(error).__name__)
                failure.not_run = status in NOT_RUN_STATUSES
                raise failure from None
