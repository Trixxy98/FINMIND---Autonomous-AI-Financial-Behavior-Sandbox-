"""ADR-0019 step 3: the Groq adapter (OpenAI-compatible Chat Completions, standard-library HTTP) on a fake transport.

Shapes follow Groq's API reference, tool-use, reasoning, and errors pages as read on 26 November 2026. No paid call.
"""

import hashlib
import json
import os
import unittest
from unittest import mock

import _path  # noqa: F401
from finmind_runtime.agents import Observation, create_agent
from finmind_runtime.budget import MARGIN_TOKENS
from finmind_runtime.claude import PROMPT_FILE, ModelApiError, find_prompts_dir, load_system_prompt
from finmind_runtime.groq import API_URL, GroqAgent, MissingCredential
from test_openai import FakeTransport

KEY = "gsk_test_not_a_real_key_0123456789"
TOOLS = [
    {"name": "get_balance", "description": "Balance.", "parameters": {"type": "object", "properties": {}}},
    {"name": "finish", "description": "Finish.", "parameters": {"type": "object", "properties": {"summary": {"type": "string"}}}},
]


def snapshot(effort="medium"):
    prompt_hash = hashlib.sha256((find_prompts_dir() / PROMPT_FILE).read_bytes()).hexdigest()
    return {"objective": "Manage funds.", "maxWallTimeSeconds": 600, "promptHash": prompt_hash, "describeUnavailableCapabilities": False,
            "agent": {"provider": "groq", "modelId": "openai/gpt-oss-120b", "settings": {"effort": effort, "maxOutputTokensPerTurn": 8_000}}}


def completion(content=None, tool_calls=None, finish="stop", usage=(1_000, 200), reasoning=None):
    message = {"role": "assistant", "content": content}
    if tool_calls is not None:
        message["tool_calls"] = tool_calls
    if reasoning is not None:
        message["reasoning"] = reasoning
    return {"id": "chatcmpl-1", "object": "chat.completion", "model": "openai/gpt-oss-120b",
            "choices": [{"index": 0, "message": message, "finish_reason": finish}],
            "usage": {"queue_time": 0.01, "prompt_tokens": usage[0], "prompt_time": 0.02, "completion_tokens": usage[1],
                      "completion_time": 0.1, "total_tokens": usage[0] + usage[1], "total_time": 0.13}}


def tool_call(call_id, name, arguments):
    return {"id": call_id, "type": "function",
            "function": {"name": name, "arguments": arguments if isinstance(arguments, str) else json.dumps(arguments)}}


def agent(transport):
    with mock.patch.dict(os.environ, {"GROQ_API_KEY": KEY}):
        a = GroqAgent(snapshot(), TOOLS, transport=transport, sleep=lambda s: None)
    reservations = []
    a.use_budget(lambda turn, attempt, bound, max_out: reservations.append((turn, attempt, bound)) or max_out)
    return a, reservations


def observe(turn, results=()):
    return Observation("objective", TOOLS, turn, None, None, list(results))


class GroqRequestTests(unittest.TestCase):
    def test_the_request_uses_only_documented_fields(self):
        transport = FakeTransport(completion(tool_calls=[tool_call("call_1", "get_balance", {})], finish="tool_calls"))
        a, reservations = agent(transport)
        a.next_action(observe(1))
        sent = transport.requests[0]
        self.assertEqual((sent["method"], sent["url"], API_URL), ("POST", API_URL, "https://api.groq.com/openai/v1/chat/completions"))
        self.assertEqual(sent["headers"]["Authorization"], f"Bearer {KEY}")
        self.assertNotIn(KEY, sent["raw"].decode())
        body = sent["body"]
        self.assertEqual(body["messages"][0], {"role": "system", "content": load_system_prompt(snapshot())})
        self.assertEqual(body["messages"][1]["role"], "user")
        self.assertEqual(body["tools"][0], {"type": "function", "function": {
            "name": "get_balance", "description": "Balance.", "parameters": {"type": "object", "properties": {}}}})
        self.assertEqual((body["model"], body["reasoning_effort"], body["max_completion_tokens"], body["tool_choice"], body["parallel_tool_calls"]),
                         ("openai/gpt-oss-120b", "medium", 8_000, "auto", False))
        for unsupported in ("store", "temperature", "top_p", "max_tokens", "n", "logprobs", "reasoning_format", "seed"):
            self.assertNotIn(unsupported, body)
        self.assertGreaterEqual(reservations[0][2], MARGIN_TOKENS)

    def test_a_missing_key_stops_before_anything_is_sent(self):
        with mock.patch.dict(os.environ, {}, clear=True), self.assertRaises(MissingCredential):
            GroqAgent(snapshot(), TOOLS, transport=FakeTransport())


class GroqTurnTests(unittest.TestCase):
    def test_calls_text_and_usage(self):
        a, _ = agent(FakeTransport(completion(content="Checking.", tool_calls=[tool_call("call_1", "get_balance", {})],
                                              finish="tool_calls", reasoning="private reasoning text")))
        decision = a.next_action(observe(1))
        self.assertEqual((decision.calls, decision.output_text), ([("get_balance", {})], "Checking."))
        self.assertEqual((decision.input_tokens, decision.cache_read_tokens, decision.output_tokens), (1_000, 0, 200))
        self.assertEqual((decision.stop_reason, decision.terminal, decision.model), ("tool_calls", None, "openai/gpt-oss-120b"))

    def test_the_assistant_turn_is_replayed_with_documented_fields_then_one_tool_message_per_call(self):
        calls = [tool_call("call_1", "get_balance", {}), tool_call("call_2", "finish", {"summary": "x"})]
        transport = FakeTransport(completion(content=None, tool_calls=calls, finish="tool_calls", reasoning="r"),
                                  completion(content="ok"))
        a, _ = agent(transport)
        a.next_action(observe(1))
        a.next_action(observe(2, [("get_balance", {"status": "ok"}), ("finish", {"status": "denied"})]))
        messages = transport.requests[1]["body"]["messages"]
        self.assertEqual(messages[2], {"role": "assistant", "content": None, "tool_calls": calls})  # no reasoning field sent back
        self.assertEqual(messages[3:], [
            {"role": "tool", "tool_call_id": "call_1", "content": json.dumps({"status": "ok"}, sort_keys=True)},
            {"role": "tool", "tool_call_id": "call_2", "content": json.dumps({"status": "denied"}, sort_keys=True)}])
        self.assertEqual(transport.requests[0]["body"]["messages"], messages[:2])

    def test_the_output_limit_is_retried_once_then_ends_the_run(self):
        transport = FakeTransport(completion(content="part", finish="length"), completion(content="part", finish="length"))
        a, reservations = agent(transport)
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, [r[1] for r in reservations], decision.input_tokens), ("max_tokens", [1, 2], 2_000))

    def test_a_content_filter_ends_the_run_as_a_refusal(self):
        a, _ = agent(FakeTransport(completion(finish="content_filter")))
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, decision.refusal_category), ("refusal", "content_filter"))

    def test_invalid_arguments_go_to_the_gateway_as_text(self):
        a, _ = agent(FakeTransport(completion(tool_calls=[tool_call("c", "transfer_funds", "{bad")], finish="tool_calls")))
        self.assertEqual(a.next_action(observe(1)).calls, [("transfer_funds", "{bad")])


class GroqErrorTests(unittest.TestCase):
    def test_rate_limits_and_flex_capacity_are_retried_under_the_same_reservation(self):
        for status in (429, 498):
            transport = FakeTransport((status, {"error": {"message": "wait", "type": "x"}}), completion(content="ok"))
            a, reservations = agent(transport)
            a.next_action(observe(1))
            self.assertEqual((len(reservations), len(transport.requests)), (1, 2), status)

    def test_server_errors_are_not_billed_and_a_422_may_have_run(self):
        # Groq: "You will not be charged for requests that return server error codes." 422 billing is not stated.
        for status, not_run in ((400, True), (401, True), (413, True), (422, False), (500, True), (502, True), (503, True)):
            a, _ = agent(FakeTransport((status, {"error": {"message": f"detail {KEY}", "type": "x"}})))
            with self.assertRaises(ModelApiError) as caught:
                a.next_action(observe(1))
            self.assertEqual((str(caught.exception), caught.exception.not_run), (f"HTTP {status}", not_run))


class GroqFactoryTests(unittest.TestCase):
    def test_create_agent_builds_the_groq_adapter(self):
        with mock.patch.dict(os.environ, {"GROQ_API_KEY": KEY}):
            self.assertIsInstance(create_agent(snapshot(), TOOLS), GroqAgent)


if __name__ == "__main__":
    unittest.main()


from test_end_to_end import API, RESEARCHER, TOKEN, config, events, http, run_until_terminal, test_worker  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class GroqEndToEndTests(unittest.TestCase):
    def test_a_run_through_the_real_api_with_a_fake_transport(self):
        transport = FakeTransport(
            completion(content="Looking.", tool_calls=[tool_call("call_1", "get_balance", {})], finish="tool_calls"),
            completion(tool_calls=[tool_call("call_2", "finish", {"summary": "Checked."})], finish="tool_calls"))
        while test_worker().run_once() is not None:
            pass
        body = config(scenario="S01")
        body.update({"maxModelTokens": 100_000, "maxCostMicroUsd": 1_000_000,
                     "agent": {"provider": "groq", "modelId": "openai/gpt-oss-120b", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        with mock.patch.dict(os.environ, {"GROQ_API_KEY": KEY}):
            worker = Worker(Client(API, TOKEN),
                            agent_factory=lambda snap, tools: GroqAgent(snap, tools, transport=transport, sleep=lambda s: None))
            run_until_terminal(run_id, worker)
        run = http("GET", f"/internal/runs/{run_id}")
        self.assertEqual((run["status"], run["configSnapshot"]["agent"]["adapter"], run["configSnapshot"]["pricingVersion"]),
                         ("COMPLETED", "groq-chat/v1", "groq-pricing/2026-11-26"))
        invocations = [e for e in events(run_id) if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        # 1,000 input x $0.15/M + 200 output x $0.60/M = 150 + 120 micro-dollars
        self.assertEqual(invocations[0]["payload"]["costMicroUsd"], 270)
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])
        self.assertNotIn(KEY, json.dumps(events(run_id)))
