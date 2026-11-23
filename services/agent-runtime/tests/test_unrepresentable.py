"""ADR-0013 item 19: a response the SDK cannot send back ends the run with an explicit reason, history unedited."""

import hashlib
import unittest
from types import SimpleNamespace

import _path  # noqa: F401
from finmind_runtime import claude, worker
from finmind_runtime.agents import Decision, Observation

BAD_INPUTS = {"lone surrogate": {"text": "x\ud800y"}, "infinity": {"amount_sen": float("inf")},
              "nan": {"n": float("nan")}, "nested": {"a": [{"b": "\udc00"}]}}


def snapshot():
    prompt = (claude.find_prompts_dir() / claude.PROMPT_FILE).read_bytes()
    return {"objective": "o", "promptHash": hashlib.sha256(prompt).hexdigest(), "maxWallTimeSeconds": 900,
            "maxActions": 10, "agent": {"provider": "anthropic", "modelId": "claude-opus-5-5",
                                        "settings": {"effort": "medium"}}}


def response(*blocks):
    return SimpleNamespace(content=list(blocks), stop_reason="tool_use", model="claude-opus-5-5", stop_details=None,
                           usage=SimpleNamespace(input_tokens=1, output_tokens=1, cache_read_input_tokens=0,
                                                 cache_creation_input_tokens=0))


def tool_use(name, value, block_id="t1"):
    return SimpleNamespace(type="tool_use", id=block_id, name=name, input=value)


class FakeSdk:
    def __init__(self, replies):
        self.replies = list(replies)
        self.messages = self

    def count_tokens(self, **kwargs):
        return SimpleNamespace(input_tokens=5)

    def create(self, **kwargs):
        return self.replies.pop(0)


def decide(*blocks):
    agent = claude.ClaudeAgent(snapshot(), [], client=FakeSdk([response(*blocks)]))
    agent.use_budget(lambda *args: 1000)
    return agent, agent.next_action(Observation("o", [], 1, None, None))


class AdapterTests(unittest.TestCase):
    def test_unrepresentable_input_ends_the_turn_and_keeps_the_calls(self):
        for label, value in BAD_INPUTS.items():
            with self.subTest(label):
                _, decision = decide(tool_use("submit_plan", value))
                self.assertEqual((decision.terminal, decision.tool_calls), ("unrepresentable", [("submit_plan", value)]))

    def test_unrepresentable_text_ends_the_turn(self):
        _, decision = decide(SimpleNamespace(type="text", text="a\ud800"), tool_use("get_balance", {}))
        self.assertEqual(decision.terminal, "unrepresentable")

    def test_history_is_not_edited(self):
        bad = tool_use("submit_plan", {"text": "x\ud800y"})
        agent, _ = decide(bad)
        self.assertIs(agent._messages[-1]["content"][0], bad)

    def test_ordinary_responses_are_unaffected(self):
        _, decision = decide(SimpleNamespace(type="text", text="genuine � \U0001F600"),
                             tool_use("submit_plan", {"text": "ok", "n": 1.5, "deep": [[{"a": None}]]}))
        self.assertIsNone(decision.terminal)


class WorkerTests(unittest.TestCase):
    def test_the_attempt_is_recorded_then_the_run_ends_with_its_own_reason(self):
        class Api:
            session_token = None
            calls, finished = [], None

            def claim(self):
                return {"runId": "r", "sessionToken": "s", "configSnapshot": snapshot()}

            def initialize(self):
                return {}

            def heartbeat(self):
                return {"status": "RUNNING"}

            def model_invocation(self, *args, **kwargs):
                return {"status": "ok"}

            def tool_call(self, call_id, tool, arguments):
                self.calls.append(tool)
                return {"status": "malformed" if tool == "submit_plan" else "ok"}

            def finish(self, outcome, reason):
                self.finished = (outcome, reason)

        api = Api()
        api.calls = []
        decision = Decision(calls=[("get_balance", {}), ("submit_plan", BAD_INPUTS["lone surrogate"])],
                            terminal="unrepresentable")
        result = worker.Worker(api, lambda *args: SimpleNamespace(next_action=lambda o: decision)).run_once()
        self.assertEqual(api.calls, ["get_balance", "submit_plan"])
        self.assertEqual(api.finished, ("failed", "unrepresentable tool input"))
        self.assertEqual(result.outcome, "failed")


class SdkTests(unittest.TestCase):
    """The real SDK with an offline mock transport: the run ends before the SDK has to send the response back."""

    def test_no_encoding_error_reaches_the_model_api(self):
        try:
            import anthropic
            import httpx2
        except ImportError:
            self.skipTest("anthropic SDK not installed")
        body = ('{"id":"m","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"tool_use",'
                '"stop_sequence":null,"content":[{"type":"tool_use","id":"t","name":"submit_plan",'
                '"input":{"text":"x\\ud800y"}}],"usage":{"input_tokens":1,"output_tokens":1}}').encode()
        requests = []

        def handler(request):
            requests.append(request.url.path)
            if request.url.path.endswith("count_tokens"):
                return httpx2.Response(200, content=b'{"input_tokens":5}', headers={"content-type": "application/json"})
            return httpx2.Response(200, content=body, headers={"content-type": "application/json"})

        sdk = anthropic.Anthropic(api_key="not-real", base_url="http://mock.invalid", max_retries=0,
                                  http_client=httpx2.Client(transport=httpx2.MockTransport(handler)))
        agent = claude.ClaudeAgent(snapshot(), [], client=sdk)
        agent.use_budget(lambda *args: 1000)
        self.assertEqual(agent.next_action(Observation("o", [], 1, None, None)).terminal, "unrepresentable")
        self.assertEqual(len(requests), 2)  # one count, one create; nothing tries to resend the history


if __name__ == "__main__":
    unittest.main()
