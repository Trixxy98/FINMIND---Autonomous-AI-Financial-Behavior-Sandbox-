"""Claude provider adapter (ADR-0012), using the official Anthropic Python SDK (pinned in requirements.txt).

A manual tool loop is used instead of the SDK tool runner because each turn must be recorded, executed through
the FINMIND gateway, and checked against server budgets before the next request.

Rules this adapter keeps:
- The conversation is append-only: each response's content is appended unchanged, then one user message holds
  every tool_result for that response. History is never edited (thinking blocks and caching depend on it).
- No automatic model fallback: a refusal ends the run and is recorded (research integrity, ADR-0012).
- Tools are not strict: malformed arguments are research observations, validated by the gateway.
- The API key is read by the SDK from the worker's environment and never passed anywhere else.
"""

from __future__ import annotations

import hashlib
import json
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


class PromptMismatch(Exception):
    """The prompt file on disk is not the one recorded in the run snapshot."""


class ModelApiError(Exception):
    """The model API failed after the SDK's retries. Only the error class name is kept."""


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


def _usage(response: Any, name: str) -> int:
    return int(getattr(response.usage, name, None) or 0)


class ClaudeAgent(Agent):
    MAX_TOKENS_ATTEMPTS = 2  # one retry with unchanged history (ADR-0012)

    def __init__(self, snapshot: Dict[str, Any], tools: List[Dict[str, Any]], client: Any = None,
                 clock: Callable[[], float] = time.monotonic) -> None:
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

    @staticmethod
    def _default_client() -> Any:
        import anthropic  # only needed for real runs
        return anthropic.Anthropic(base_url=API_BASE_URL, max_retries=2, timeout=600.0)

    def next_action(self, observation: Observation) -> Decision:
        if self._pending:
            self._append_tool_results(observation.results)
        elif observation.turn > 1:
            self._messages.append({"role": "user", "content": CONTINUATION})

        totals = {"input": 0, "output": 0, "cache_read": 0, "cache_creation": 0, "latency": 0}
        response = None
        for _ in range(self.MAX_TOKENS_ATTEMPTS):
            started = self._clock()
            response = self._create()
            totals["latency"] += int((self._clock() - started) * 1000)
            totals["input"] += _usage(response, "input_tokens")
            totals["output"] += _usage(response, "output_tokens")
            totals["cache_read"] += _usage(response, "cache_read_input_tokens")
            totals["cache_creation"] += _usage(response, "cache_creation_input_tokens")
            if response.stop_reason != "max_tokens":
                break

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
        return Decision(calls=[(block.name, block.input) for block in tool_uses], output_text=text, **common)

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

    def _create(self) -> Any:
        try:
            return self._client.messages.create(
                model=self._model,
                max_tokens=self._max_tokens,
                system=self._system,
                tools=self._tools,
                messages=self._messages,
                thinking={"type": "adaptive"},
                output_config={"effort": self._effort},
                cache_control={"type": "ephemeral"},
            )
        except Exception as error:  # the SDK already retried 429, 5xx, and connection errors
            raise ModelApiError(type(error).__name__) from None
