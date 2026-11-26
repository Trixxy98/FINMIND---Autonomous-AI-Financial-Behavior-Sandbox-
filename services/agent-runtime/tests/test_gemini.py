"""ADR-0019 step 4: the Gemini adapter (Interactions API, store=false, standard-library HTTP) on a fake transport.

Shapes follow Google's Interactions API reference, function-calling, thinking, and API errors pages (aistudio.google.com),
read on 26 November 2026. The function_call step's full JSON is not shown verbatim there; the fake follows the
documented field names (type, id, name, arguments as an object) and the verbatim function_result example. No paid call.
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
from finmind_runtime.gemini import API_URL, GeminiAgent, MissingCredential
from test_openai import FakeTransport

KEY = "AIza-test-not-a-real-key-0123456789"
TOOLS = [
    {"name": "get_balance", "description": "Balance.", "parameters": {"type": "object", "properties": {}}},
    {"name": "finish", "description": "Finish.", "parameters": {"type": "object", "properties": {"summary": {"type": "string"}}}},
]
THOUGHT = {"type": "thought", "signature": "opaque-signature-abc", "summary": [{"type": "text", "text": "Plan."}]}


def snapshot(effort="medium"):
    prompt_hash = hashlib.sha256((find_prompts_dir() / PROMPT_FILE).read_bytes()).hexdigest()
    return {"objective": "Manage funds.", "maxWallTimeSeconds": 600, "promptHash": prompt_hash, "describeUnavailableCapabilities": False,
            "agent": {"provider": "google", "modelId": "gemini-4-hydrogen", "settings": {"effort": effort, "maxOutputTokensPerTurn": 8_000}}}


def interaction(steps, status="completed", usage=(1_000, 120, 80, 300)):
    inp, out, thought, cached = usage
    return {"id": "int_1", "object": "interaction", "model": "gemini-4-hydrogen", "status": status, "steps": steps,
            "usage": {"total_input_tokens": inp, "total_output_tokens": out, "total_thought_tokens": thought,
                      "total_cached_tokens": cached, "total_tool_use_tokens": 0, "total_tokens": inp + out + thought}}


def fcall(call_id, name, arguments):
    return {"type": "function_call", "id": call_id, "name": name, "arguments": arguments}


def text(t):
    return {"type": "model_output", "content": [{"type": "text", "text": t}]}


def agent(transport):
    with mock.patch.dict(os.environ, {"GEMINI_API_KEY": KEY}):
        a = GeminiAgent(snapshot(), TOOLS, transport=transport, sleep=lambda s: None)
    reservations = []
    a.use_budget(lambda turn, attempt, bound, max_out: reservations.append((turn, attempt, bound)) or max_out)
    return a, reservations


def observe(turn, results=()):
    return Observation("objective", TOOLS, turn, None, None, list(results))


class GeminiRequestTests(unittest.TestCase):
    def test_the_request_is_stateless_unstored_and_keyed_by_header(self):
        transport = FakeTransport(interaction([THOUGHT, fcall("call_1", "get_balance", {})]))
        a, reservations = agent(transport)
        a.next_action(observe(1))
        sent = transport.requests[0]
        self.assertEqual((sent["method"], sent["url"], API_URL),
                         ("POST", API_URL, "https://generativelanguage.googleapis.com/v1beta/interactions"))
        self.assertEqual(sent["headers"]["x-goog-api-key"], KEY)
        self.assertNotIn(KEY, sent["url"])  # never in the URL, unlike the ?key= examples
        self.assertNotIn(KEY, sent["raw"].decode())
        body = sent["body"]
        self.assertIs(body["store"], False)
        for absent in ("previous_interaction_id", "background", "temperature"):
            self.assertNotIn(absent, body)
        self.assertNotIn("temperature", body["generation_config"])
        self.assertEqual(body["system_instruction"], load_system_prompt(snapshot()))
        self.assertEqual(body["generation_config"], {"max_output_tokens": 8_000, "thinking_level": "medium", "tool_choice": "auto"})
        self.assertEqual(body["tools"][0], {"type": "function", "name": "get_balance", "description": "Balance.",
                                            "parameters": {"type": "object", "properties": {}}})
        self.assertEqual(body["input"][0]["type"], "user_input")
        self.assertGreaterEqual(reservations[0][2], MARGIN_TOKENS)

    def test_a_missing_key_stops_before_anything_is_sent(self):
        with mock.patch.dict(os.environ, {}, clear=True), self.assertRaises(MissingCredential):
            GeminiAgent(snapshot(), TOOLS, transport=FakeTransport())


class GeminiTurnTests(unittest.TestCase):
    def test_calls_text_and_usage(self):
        a, _ = agent(FakeTransport(interaction([THOUGHT, text("Checking."), fcall("call_1", "get_balance", {}),
                                                fcall("call_2", "finish", {"summary": "done"})])))
        decision = a.next_action(observe(1))
        self.assertEqual(decision.calls, [("get_balance", {}), ("finish", {"summary": "done"})])
        self.assertEqual(decision.output_text, "Checking.")
        # Billed output is output plus thinking tokens. Whether cached tokens are inside total_input_tokens is not
        # stated, so input is not reduced by them: the recorded cost can only be too high, never too low.
        self.assertEqual((decision.input_tokens, decision.cache_read_tokens, decision.output_tokens), (1_000, 300, 200))

    def test_every_step_is_replayed_unchanged_then_one_function_result_per_call(self):
        first = [THOUGHT, fcall("call_1", "get_balance", {}), fcall("call_2", "finish", {"summary": "x"})]
        transport = FakeTransport(interaction(first), interaction([text("ok")]))
        a, _ = agent(transport)
        a.next_action(observe(1))
        a.next_action(observe(2, [("get_balance", {"status": "ok"}), ("finish", {"status": "denied"})]))
        sent = transport.requests[1]["body"]["input"]
        self.assertEqual(sent[1:4], first)  # the thought step with its signature, exactly as received
        self.assertEqual(sent[4:], [
            {"type": "function_result", "name": "get_balance", "call_id": "call_1",
             "result": [{"type": "text", "text": json.dumps({"status": "ok"}, sort_keys=True)}]},
            {"type": "function_result", "name": "finish", "call_id": "call_2",
             "result": [{"type": "text", "text": json.dumps({"status": "denied"}, sort_keys=True)}]}])
        self.assertEqual(transport.requests[0]["body"]["input"], sent[:1])

    def test_a_turn_without_calls_gets_the_fixed_continuation(self):
        transport = FakeTransport(interaction([text("Thinking.")]), interaction([text("Still.")]))
        a, _ = agent(transport)
        a.next_action(observe(1))
        a.next_action(observe(2))
        self.assertEqual(transport.requests[1]["body"]["input"][-1], {"type": "user_input", "content": [
            {"type": "text", "text": "The simulation is still running. Use the tools to act, wait, or finish."}]})

    def test_string_arguments_are_parsed_and_bad_ones_go_to_the_gateway_as_text(self):
        a, _ = agent(FakeTransport(interaction([fcall("c", "transfer_funds", '{"amount_sen": 5}'), fcall("d", "wait", "{bad")])))
        self.assertEqual(a.next_action(observe(1)).calls, [("transfer_funds", {"amount_sen": 5}), ("wait", "{bad")])

    def test_an_incomplete_interaction_is_retried_once_then_ends_the_run(self):
        transport = FakeTransport(interaction([THOUGHT], status="incomplete"), interaction([THOUGHT], status="incomplete"))
        a, reservations = agent(transport)
        decision = a.next_action(observe(1))
        self.assertEqual((decision.terminal, [r[1] for r in reservations]), ("max_tokens", [1, 2]))
        self.assertEqual(transport.requests[0]["body"]["input"], transport.requests[1]["body"]["input"])
        self.assertEqual(decision.output_tokens, 400)  # thinking tokens of both attempts are billed


class GeminiErrorTests(unittest.TestCase):
    def test_a_rate_limit_is_retried_under_the_same_reservation(self):
        transport = FakeTransport((429, {"error": {"code": "rate_limit_exceeded", "message": "wait"}}), interaction([text("ok")]))
        a, reservations = agent(transport)
        a.next_action(observe(1))
        self.assertEqual((len(reservations), len(transport.requests)), (1, 2))

    def test_blocked_generations_end_the_run_with_the_documented_code_and_keep_their_reservation(self):
        for code in ("safety", "prohibited_content", "malformed_function_call", "missing_thought_signature"):
            a, _ = agent(FakeTransport((400, {"error": {"code": code, "message": f"detail {KEY}"}})))
            with self.assertRaises(ModelApiError) as caught:
                a.next_action(observe(1))
            self.assertEqual((str(caught.exception), caught.exception.not_run), (f"HTTP 400 {code}", False))
            self.assertNotIn(KEY, str(caught.exception))

    def test_other_errors_keep_the_status_and_say_whether_the_request_could_have_run(self):
        for status, code, not_run in ((400, "invalid_request", True), (401, "authentication", True), (402, "payment_required", True),
                                      (403, "permission_denied", True), (404, "model_not_found", True),
                                      (500, "api_error", False), (503, "service_unavailable", False)):
            a, _ = agent(FakeTransport((status, {"error": {"code": code, "message": "x"}})))
            with self.assertRaises(ModelApiError) as caught:
                a.next_action(observe(1))
            self.assertEqual((str(caught.exception), caught.exception.not_run), (f"HTTP {status} {code}", not_run))

    def test_a_failed_interaction_status_ends_the_run(self):
        a, _ = agent(FakeTransport(interaction([], status="failed")))
        with self.assertRaises(ModelApiError) as caught:
            a.next_action(observe(1))
        self.assertEqual((str(caught.exception), caught.exception.not_run), ("status failed", False))


class GeminiFactoryTests(unittest.TestCase):
    def test_create_agent_builds_the_gemini_adapter(self):
        with mock.patch.dict(os.environ, {"GEMINI_API_KEY": KEY}):
            self.assertIsInstance(create_agent(snapshot(), TOOLS), GeminiAgent)


if __name__ == "__main__":
    unittest.main()


from test_end_to_end import API, RESEARCHER, TOKEN, config, events, http, run_until_terminal, test_worker  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class GeminiEndToEndTests(unittest.TestCase):
    def test_a_run_through_the_real_api_with_a_fake_transport(self):
        transport = FakeTransport(interaction([THOUGHT, text("Looking."), fcall("call_1", "get_balance", {})]),
                                  interaction([fcall("call_2", "finish", {"summary": "Checked."})]))
        while test_worker().run_once() is not None:
            pass
        body = config(scenario="S01")
        body.update({"maxModelTokens": 100_000, "maxCostMicroUsd": 1_000_000,
                     "agent": {"provider": "google", "modelId": "gemini-4-hydrogen", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        with mock.patch.dict(os.environ, {"GEMINI_API_KEY": KEY}):
            worker = Worker(Client(API, TOKEN),
                            agent_factory=lambda snap, tools: GeminiAgent(snap, tools, transport=transport, sleep=lambda s: None))
            run_until_terminal(run_id, worker)
        run = http("GET", f"/internal/runs/{run_id}")
        self.assertEqual((run["status"], run["configSnapshot"]["agent"]["adapter"], run["configSnapshot"]["pricingVersion"]),
                         ("COMPLETED", "gemini-interactions/v1", "google-pricing/2026-11-26"))
        invocations = [e for e in events(run_id) if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        # 1,000 input x $1.50/M + 300 cached x $0.075/M + 200 output (incl. thinking) x $7.50/M = 3,022.5, rounded up
        self.assertEqual(invocations[0]["payload"]["costMicroUsd"], 3_023)
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])
        self.assertIn(THOUGHT, transport.requests[1]["body"]["input"])
        self.assertNotIn(KEY, json.dumps(events(run_id)))
