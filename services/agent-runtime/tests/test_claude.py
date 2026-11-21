"""ClaudeAgent tests with a fake SDK client: no network, no cost (ADR-0012)."""

import copy
import hashlib
import json
import unittest
from types import SimpleNamespace

import _path  # noqa: F401
from finmind_runtime.claude import (CONTINUATION, PROMPT_FILE, UNAVAILABLE_TEXT, ClaudeAgent, ModelApiError,
                                    PromptMismatch, find_prompts_dir)
from finmind_runtime.agents import Observation

TOOLS = [
    {"name": "get_balance", "description": "Balances.", "parameters": {"type": "object", "additionalProperties": False, "required": [], "properties": {}}},
    {"name": "wait", "description": "Wait.", "parameters": {"type": "object", "additionalProperties": False, "required": ["days"],
                                                           "properties": {"days": {"type": "integer"}}}},
]


def snapshot(describe=False, effort="medium"):
    prompt_hash = hashlib.sha256((find_prompts_dir() / PROMPT_FILE).read_bytes()).hexdigest()
    return {"objective": "Manage funds.", "promptHash": prompt_hash, "describeUnavailableCapabilities": describe,
            "agent": {"provider": "anthropic", "modelId": "claude-opus-5-5",
                      "settings": {"effort": effort, "maxOutputTokensPerTurn": 16000}}}


def block(kind, **fields):
    return SimpleNamespace(type=kind, **fields)


def response(content, stop_reason="tool_use", category=None, **usage):
    defaults = {"input_tokens": 100, "output_tokens": 20, "cache_read_input_tokens": None, "cache_creation_input_tokens": 50}
    defaults.update(usage)
    return SimpleNamespace(content=content, stop_reason=stop_reason, model="claude-opus-5-5",
                           stop_details=SimpleNamespace(category=category) if category else None,
                           usage=SimpleNamespace(**defaults))


class FakeMessages:
    def __init__(self, responses):
        self.responses = list(responses)
        self.requests = []

    def create(self, **kwargs):
        self.requests.append(copy.deepcopy(kwargs))
        outcome = self.responses.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


def agent_with(responses, **kwargs):
    messages = FakeMessages(responses)
    agent = ClaudeAgent(snapshot(**kwargs), TOOLS, client=SimpleNamespace(messages=messages), clock=lambda: 0.0)
    return agent, messages


def observe(turn, results=()):
    results = list(results)
    last = results[-1] if results else (None, None)
    return Observation("Manage funds.", TOOLS, turn, last[0], last[1], results)


class RequestShapeTests(unittest.TestCase):
    def test_request_uses_documented_parameters_only(self):
        agent, messages = agent_with([response([block("text", text="hi")], stop_reason="end_turn")])
        agent.next_action(observe(1))
        request = messages.requests[0]
        self.assertEqual(request["model"], "claude-opus-5-5")
        self.assertEqual(request["max_tokens"], 16000)
        self.assertEqual(request["thinking"], {"type": "adaptive"})
        self.assertEqual(request["output_config"], {"effort": "medium"})
        self.assertEqual(request["cache_control"], {"type": "ephemeral"})
        self.assertNotIn("temperature", request)
        self.assertNotIn("tool_choice", request)
        self.assertEqual(request["tools"][1], {"name": "wait", "description": "Wait.", "input_schema": TOOLS[1]["parameters"]})
        self.assertNotIn("strict", request["tools"][0])
        self.assertIn("Manage funds.", request["messages"][0]["content"])

    def test_unavailable_capabilities_are_described_only_when_configured(self):
        described, messages = agent_with([response([], stop_reason="end_turn")], describe=True)
        described.next_action(observe(1))
        self.assertIn(UNAVAILABLE_TEXT, messages.requests[0]["system"])
        plain, messages = agent_with([response([], stop_reason="end_turn")], describe=False)
        plain.next_action(observe(1))
        self.assertNotIn("create_money", messages.requests[0]["system"])
        self.assertNotIn("{{", messages.requests[0]["system"])

    def test_prompt_hash_mismatch_is_refused(self):
        changed = snapshot()
        changed["promptHash"] = "0" * 64
        with self.assertRaises(PromptMismatch):
            ClaudeAgent(changed, TOOLS, client=SimpleNamespace(messages=FakeMessages([])))


class LoopTests(unittest.TestCase):
    def test_several_tool_uses_then_paired_results_in_one_message(self):
        first = [block("text", text="Checking."), block("thinking", thinking=""),
                 block("tool_use", id="tu_1", name="get_balance", input={}),
                 block("tool_use", id="tu_2", name="wait", input={"days": "x"})]
        agent, messages = agent_with([response(first), response([block("text", text="ok")], stop_reason="end_turn")])
        decision = agent.next_action(observe(1))
        self.assertEqual(decision.tool_calls, [("get_balance", {}), ("wait", {"days": "x"})])
        self.assertEqual((decision.output_text, decision.cache_creation_tokens, decision.cache_read_tokens), ("Checking.", 50, 0))

        agent.next_action(observe(2, [("get_balance", {"status": "ok", "data": {"cash_sen": 1}}),
                                      ("wait", {"status": "malformed", "reason_code": "INVALID_TOOL_ARGUMENTS"})]))
        history = messages.requests[1]["messages"]
        self.assertEqual(history[1]["role"], "assistant")
        self.assertEqual([b.type for b in history[1]["content"]], ["text", "thinking", "tool_use", "tool_use"])
        results = history[2]["content"]
        self.assertEqual([r["tool_use_id"] for r in results], ["tu_1", "tu_2"])
        self.assertNotIn("is_error", results[0])
        self.assertTrue(results[1]["is_error"])
        self.assertEqual(json.loads(results[0]["content"])["data"]["cash_sen"], 1)
        # Earlier requests are a prefix of later ones (append-only history).
        self.assertEqual(len(messages.requests[0]["messages"]), 1)

    def test_turn_without_tools_gets_the_fixed_continuation(self):
        agent, messages = agent_with([response([block("text", text="Thinking about it.")], stop_reason="end_turn"),
                                      response([], stop_reason="end_turn")])
        self.assertEqual(agent.next_action(observe(1)).tool_calls, [])
        agent.next_action(observe(2))
        self.assertEqual(messages.requests[1]["messages"][-1], {"role": "user", "content": CONTINUATION})

    def test_max_tokens_is_retried_once_without_changing_history(self):
        agent, messages = agent_with([response([block("text", text="cut")], stop_reason="max_tokens"),
                                      response([block("tool_use", id="tu_1", name="get_balance", input={})])])
        decision = agent.next_action(observe(1))
        self.assertEqual(decision.tool_calls, [("get_balance", {})])
        self.assertEqual(decision.input_tokens, 200)
        self.assertEqual(messages.requests[0]["messages"], messages.requests[1]["messages"])

        agent, _ = agent_with([response([], stop_reason="max_tokens"), response([], stop_reason="max_tokens")])
        self.assertEqual(agent.next_action(observe(1)).terminal, "max_tokens")

    def test_refusal_is_terminal_and_never_falls_back(self):
        agent, messages = agent_with([response([], stop_reason="refusal", category="cyber")])
        decision = agent.next_action(observe(1))
        self.assertEqual((decision.terminal, decision.refusal_category, decision.tool_calls), ("refusal", "cyber", []))
        self.assertNotIn("fallbacks", messages.requests[0])
        self.assertNotIn("betas", messages.requests[0])

    def test_api_errors_keep_only_the_class_name(self):
        class RateLimitError(Exception):
            pass
        agent, _ = agent_with([RateLimitError("sk-ant-secret in body")])
        with self.assertRaises(ModelApiError) as error:
            agent.next_action(observe(1))
        self.assertEqual(str(error.exception), "RateLimitError")


class SdkCompatibilityTests(unittest.TestCase):
    """Checks parameter names against the pinned SDK without any network call (skipped if it is not installed)."""

    def test_pinned_sdk_accepts_the_request_parameters(self):
        try:
            import anthropic
        except ImportError:
            self.skipTest("anthropic SDK not installed (pip install -r services/agent-runtime/requirements.txt)")
        import inspect
        client = anthropic.Anthropic(api_key="not-a-real-key", base_url="https://api.anthropic.com")
        parameters = inspect.signature(client.messages.create).parameters
        for name in ("model", "max_tokens", "system", "tools", "messages", "thinking", "output_config", "cache_control"):
            self.assertIn(name, parameters)


if __name__ == "__main__":
    unittest.main()
