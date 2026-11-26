"""ADR-0019 step 2: the OpenAI adapter (Responses API, standard-library HTTP) against a fake transport. No paid call.

Response shapes follow OpenAI's Responses API reference and function-calling guide as read on 26 November 2026.
"""

import hashlib
import json
import os
import unittest
from unittest import mock

import _path  # noqa: F401
from finmind_runtime.agents import Observation, create_agent
from finmind_runtime.budget import MARGIN_TOKENS, request_bytes
from finmind_runtime.claude import PROMPT_FILE, ModelApiError, WallTimeReached, find_prompts_dir, load_system_prompt
from finmind_runtime.openai import API_URL, MissingCredential, OpenAIAgent

KEY = "sk-test-not-a-real-key-0123456789"
TOOLS = [
    {"name": "get_balance", "description": "Balance.", "parameters": {"type": "object", "properties": {}}},
    {"name": "finish", "description": "Finish.", "parameters": {"type": "object", "properties": {"summary": {"type": "string"}}}},
]


def snapshot(effort="medium", wall=600):
    prompt_hash = hashlib.sha256((find_prompts_dir() / PROMPT_FILE).read_bytes()).hexdigest()
    return {"objective": "Manage funds.", "maxWallTimeSeconds": wall, "promptHash": prompt_hash,
            "describeUnavailableCapabilities": False,
            "agent": {"provider": "openai", "modelId": "gpt-6.2-sol", "settings": {"effort": effort, "maxOutputTokensPerTurn": 8_000}}}


def response(output, status="completed", incomplete=None, usage=(1_000, 200, 300, 50), model="gpt-6.2-sol"):
    inp, out, cached, reasoning = usage
    return {"id": "resp_1", "object": "response", "status": status, "model": model,
            "incomplete_details": {"reason": incomplete} if incomplete else None, "output": output,
            "usage": {"input_tokens": inp, "input_tokens_details": {"cached_tokens": cached}, "output_tokens": out,
                      "output_tokens_details": {"reasoning_tokens": reasoning}, "total_tokens": inp + out}}


def call(call_id, name, arguments):
    return {"type": "function_call", "id": f"fc_{call_id}", "call_id": call_id, "name": name,
            "arguments": arguments if isinstance(arguments, str) else json.dumps(arguments), "status": "completed"}


REASONING = {"type": "reasoning", "id": "rs_1", "summary": [], "encrypted_content": "opaque-reasoning-blob"}


def message(text):
    return {"type": "message", "id": "msg_1", "role": "assistant", "status": "completed",
            "content": [{"type": "output_text", "text": text, "annotations": []}]}


class FakeTransport:
    def __init__(self, *replies):
        self.replies = list(replies)
        self.requests = []

    def __call__(self, method, url, headers, body, timeout):
        self.requests.append({"method": method, "url": url, "headers": headers, "body": json.loads(body), "raw": body})
        reply = self.replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        status, payload = reply if isinstance(reply, tuple) else (200, reply)
        return status, json.dumps(payload).encode()


def agent(transport, effort="medium", clock=None, reserve=None):
    with mock.patch.dict(os.environ, {"OPENAI_API_KEY": KEY}):
        a = OpenAIAgent(snapshot(effort), TOOLS, transport=transport, sleep=lambda s: None,
                        **({"clock": clock} if clock else {}))
    reservations = []
    a.use_budget(reserve or (lambda turn, attempt, bound, max_out: reservations.append((turn, attempt, bound, max_out)) or max_out))
    return a, reservations


def observe(turn, results=()):
    return Observation("objective", TOOLS, turn, None, None, list(results))


class RequestTests(unittest.TestCase):
    def test_the_request_is_stateless_unstored_and_carries_the_same_prompt_and_tools(self):
        transport = FakeTransport(response([message("Checking."), call("c1", "get_balance", {})]))
        a, reservations = agent(transport)
        a.next_action(observe(1))
        sent = transport.requests[0]
        self.assertEqual((sent["method"], sent["url"]), ("POST", API_URL))
        self.assertEqual(API_URL, "https://api.openai.com/v1/responses")
        self.assertEqual(sent["headers"]["Authorization"], f"Bearer {KEY}")
        self.assertNotIn(KEY, sent["raw"].decode())
        self.assertNotIn(KEY, sent["url"])
        body = sent["body"]
        self.assertIs(body["store"], False)
        self.assertNotIn("previous_response_id", body)
        self.assertNotIn("temperature", body)
        self.assertNotIn("top_p", body)
        self.assertEqual(body["model"], "gpt-6.2-sol")
        self.assertEqual(body["instructions"], load_system_prompt(snapshot()))
        self.assertEqual(body["reasoning"], {"effort": "medium"})
        self.assertEqual(body["max_output_tokens"], 8_000)
        self.assertEqual(body["tools"], [
            {"type": "function", "name": "get_balance", "description": "Balance.", "parameters": {"type": "object", "properties": {}}, "strict": False},
            {"type": "function", "name": "finish", "description": "Finish.",
             "parameters": {"type": "object", "properties": {"summary": {"type": "string"}}}, "strict": False}])
        self.assertEqual(body["input"][0]["role"], "user")
        self.assertIn("objective", body["input"][0]["content"].lower())
        # The reservation used the byte bound of the request it was about to send.
        turn, attempt, bound, _ = reservations[0]
        self.assertEqual((turn, attempt), (1, 1))
        self.assertGreaterEqual(bound, len(request_bytes({k: v for k, v in body.items() if k != "max_output_tokens"})))
        self.assertGreaterEqual(bound, MARGIN_TOKENS)

    def test_no_request_is_sent_when_the_reservation_is_refused(self):
        transport = FakeTransport()

        def refuse(*_):
            raise RuntimeError("denied")
        a, _ = agent(transport, reserve=refuse)
        with self.assertRaises(RuntimeError):
            a.next_action(observe(1))
        self.assertEqual(transport.requests, [])

    def test_a_missing_key_stops_before_any_reservation_or_request(self):
        with mock.patch.dict(os.environ, {}, clear=True), self.assertRaises(MissingCredential):
            OpenAIAgent(snapshot(), TOOLS, transport=FakeTransport())


class TurnTests(unittest.TestCase):
    def test_calls_text_and_usage_become_one_decision(self):
        a, _ = agent(FakeTransport(response([REASONING, message("Checking."), call("c1", "get_balance", {}),
                                             call("c2", "finish", {"summary": "done"})])))
        decision = a.next_action(observe(1))
        self.assertEqual(decision.calls, [("get_balance", {}), ("finish", {"summary": "done"})])
        self.assertEqual(decision.output_text, "Checking.")
        # input_tokens includes cached tokens; FINMIND prices them apart. Reasoning tokens are billed as output.
        self.assertEqual((decision.input_tokens, decision.cache_read_tokens, decision.cache_creation_tokens, decision.output_tokens),
                         (700, 300, 0, 200))
        self.assertEqual((decision.model, decision.stop_reason, decision.terminal), ("gpt-6.2-sol", "completed", None))

    def test_the_whole_output_is_replayed_unchanged_then_one_result_per_call(self):
        first = [REASONING, call("c1", "get_balance", {}), call("c2", "finish", {"summary": "x"})]
        transport = FakeTransport(response(first), response([message("ok")]))
        a, _ = agent(transport)
        a.next_action(observe(1))
        a.next_action(observe(2, [("get_balance", {"status": "ok", "data": {"cash_sen": 5}}),
                                  ("finish", {"status": "denied", "reason_code": "X"})]))
        sent = transport.requests[1]["body"]["input"]
        self.assertEqual(sent[1:4], first)  # reasoning (with its encrypted content) and calls, exactly as returned
        self.assertEqual(sent[4:], [
            {"type": "function_call_output", "call_id": "c1", "output": json.dumps({"status": "ok", "data": {"cash_sen": 5}}, sort_keys=True)},
            {"type": "function_call_output", "call_id": "c2", "output": json.dumps({"status": "denied", "reason_code": "X"}, sort_keys=True)}])
        self.assertEqual(transport.requests[0]["body"]["input"], sent[:1])  # earlier history never edited

    def test_a_turn_without_calls_gets_the_fixed_continuation(self):
        transport = FakeTransport(response([message("Thinking.")]), response([message("Still.")]))
        a, _ = agent(transport)
        a.next_action(observe(1))
        a.next_action(observe(2))
        last = transport.requests[1]["body"]["input"][-1]
        self.assertEqual(last, {"role": "user", "content": "The simulation is still running. Use the tools to act, wait, or finish."})

    def test_invalid_json_arguments_are_passed_on_for_the_gateway_to_record(self):
        a, _ = agent(FakeTransport(response([call("c1", "transfer_funds", '{"amount_sen": 5')])))
        self.assertEqual(a.next_action(observe(1)).calls, [("transfer_funds", '{"amount_sen": 5')])

    def test_arguments_that_cannot_be_sent_back_end_the_turn_after_its_calls(self):
        a, _ = agent(FakeTransport(response([call("c1", "get_balance", '{"note": "\\ud800"}')])))
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, len(decision.calls)), ("unrepresentable", 1))


class StopTests(unittest.TestCase):
    def test_the_output_limit_is_retried_once_with_history_unchanged_then_ends_the_run(self):
        transport = FakeTransport(response([REASONING], status="incomplete", incomplete="max_output_tokens"),
                                  response([REASONING], status="incomplete", incomplete="max_output_tokens"))
        a, reservations = agent(transport)
        decision = a.next_action(observe(1))
        self.assertEqual(decision.terminal, "max_tokens")
        self.assertEqual([r[1] for r in reservations], [1, 2])
        self.assertEqual(transport.requests[0]["body"]["input"], transport.requests[1]["body"]["input"])
        self.assertEqual(decision.input_tokens + decision.cache_read_tokens, 2_000)  # both attempts were billed

    def test_a_content_filter_or_a_refusal_ends_the_run_without_fallback(self):
        a, _ = agent(FakeTransport(response([], status="incomplete", incomplete="content_filter")))
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, decision.refusal_category), ("refusal", "content_filter"))
        refusal = {"type": "message", "id": "m", "role": "assistant", "status": "completed",
                   "content": [{"type": "refusal", "refusal": "I can't help with that."}]}
        a, _ = agent(FakeTransport(response([refusal])))
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, decision.refusal_category), ("refusal", "refusal"))


class ErrorTests(unittest.TestCase):
    def test_a_rate_limit_is_retried_under_the_same_reservation(self):
        transport = FakeTransport((429, {"error": {"code": "rate_limit_exceeded", "message": "slow down"}}),
                                  response([message("ok")]))
        a, reservations = agent(transport)
        a.next_action(observe(1))
        self.assertEqual((len(reservations), len(transport.requests)), (1, 2))

    def test_errors_keep_only_the_status_and_say_whether_the_request_could_have_run(self):
        for status, not_run in ((400, True), (401, True), (404, True), (500, False), (503, False)):
            a, _ = agent(FakeTransport((status, {"error": {"message": f"secret detail {KEY}"}})))
            with self.assertRaises(ModelApiError) as caught:
                a.next_action(observe(1))
            self.assertEqual((str(caught.exception), caught.exception.not_run), (f"HTTP {status}", not_run))
            self.assertNotIn(KEY, str(caught.exception))
        a, _ = agent(FakeTransport(TimeoutError("timed out")))
        with self.assertRaises(ModelApiError) as caught:
            a.next_action(observe(1))
        self.assertEqual((str(caught.exception), caught.exception.not_run), ("TimeoutError", False))

    def test_no_request_is_reserved_without_wall_time_left(self):
        times = iter([0.0, 10_000.0])
        transport = FakeTransport()
        a, reservations = agent(transport, clock=lambda: next(times, 10_000.0))
        with self.assertRaises(WallTimeReached):
            a.next_action(observe(1))
        self.assertEqual((reservations, transport.requests), ([], []))


class FactoryTests(unittest.TestCase):
    def test_create_agent_builds_the_openai_adapter(self):
        with mock.patch.dict(os.environ, {"OPENAI_API_KEY": KEY}):
            self.assertIsInstance(create_agent(snapshot(), TOOLS), OpenAIAgent)


if __name__ == "__main__":
    unittest.main()


from test_end_to_end import API, RESEARCHER, TOKEN, config, events, http, run_until_terminal, test_worker  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class OpenAIEndToEndTests(unittest.TestCase):
    def test_a_run_through_the_real_api_with_a_fake_transport(self):
        transport = FakeTransport(
            response([REASONING, message("Let me look around."), call("c1", "get_balance", {}), call("c2", "get_environment", {})]),
            response([call("c3", "finish", {"summary": "Balance checked."})]))
        while test_worker().run_once() is not None:  # this transport serves only this run
            pass
        body = config(scenario="S01")
        body.update({"maxModelTokens": 100_000, "maxCostMicroUsd": 1_000_000,
                     "agent": {"provider": "openai", "modelId": "gpt-6.2-sol", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        with mock.patch.dict(os.environ, {"OPENAI_API_KEY": KEY}):
            worker = Worker(Client(API, TOKEN),
                            agent_factory=lambda snap, tools: OpenAIAgent(snap, tools, transport=transport, sleep=lambda s: None))
            run_until_terminal(run_id, worker)

        run = http("GET", f"/internal/runs/{run_id}")
        self.assertEqual(run["status"], "COMPLETED")
        agent_snapshot = run["configSnapshot"]["agent"]
        self.assertEqual((agent_snapshot["adapter"], agent_snapshot["tokenBound"], run["configSnapshot"]["pricingVersion"]),
                         ("openai-responses/v1", "utf8-bytes+1024/v1", "openai-pricing/2026-11-26"))
        evs = events(run_id)
        invocations = [e for e in evs if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        # 700 input x $2/M + 300 cached x $0.10/M + 200 output x $10/M = 1,400 + 30 + 2,000 micro-dollars
        self.assertEqual(invocations[0]["payload"]["costMicroUsd"], 3_430)
        self.assertEqual((invocations[0]["payload"]["toolUseCount"], invocations[0]["payload"]["outputText"]),
                         (2, "Let me look around."))
        traces = [e["traceId"] for e in evs if e["eventType"] == "TOOL_CALL_PROPOSED"]
        self.assertEqual(traces, ["turn-0001-1", "turn-0001-2", "turn-0002"])
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])
        # The second request carried the first turn's output unchanged, then both results.
        second = transport.requests[1]["body"]["input"]
        self.assertEqual([i.get("type") for i in second[-2:]], ["function_call_output", "function_call_output"])
        self.assertIn(REASONING, second)
        self.assertNotIn(KEY, json.dumps(evs))
