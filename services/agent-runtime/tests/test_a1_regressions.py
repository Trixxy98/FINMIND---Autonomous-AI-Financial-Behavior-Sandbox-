"""ADR-0013 A1 regression tests: cost cap, sessions, and worker crash containment. No paid calls."""

import contextlib
import hashlib
import json
import os
import sys
import time
import unittest
from types import SimpleNamespace
from unittest import mock

import _path  # noqa: F401
from finmind_runtime import claude, worker
from finmind_runtime.agents import Decision, Observation
from finmind_runtime.client import ApiError, Client, SessionEnded, Unreachable

SNAPSHOT = {"objective": "o", "maxWallTimeSeconds": 900, "maxActions": 20, "agent": {"provider": "mock"}}


def claude_snapshot(**changes):
    prompt = (claude.find_prompts_dir() / claude.PROMPT_FILE).read_bytes()
    snapshot = {"objective": "o", "promptHash": hashlib.sha256(prompt).hexdigest(), "maxWallTimeSeconds": 900,
                "agent": {"modelId": "claude-opus-5-5", "settings": {"effort": "medium", "maxOutputTokensPerTurn": 16000}}}
    snapshot.update(changes)
    return snapshot


def reply(stop="end_turn"):
    return SimpleNamespace(content=[], stop_reason=stop, model="claude-opus-5-5", stop_details=None,
                           usage=SimpleNamespace(input_tokens=1000, output_tokens=50,
                                                 cache_read_input_tokens=0, cache_creation_input_tokens=0))


class FakeSdk:
    def __init__(self, outcomes):
        self.outcomes = list(outcomes)
        self.log = []
        self.messages = self

    def count_tokens(self, **kwargs):
        self.log.append(("count", kwargs))
        return SimpleNamespace(input_tokens=1000)

    def create(self, **kwargs):
        self.log.append(("create", kwargs))
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome

    @property
    def creates(self):
        return [kwargs for name, kwargs in self.log if name == "create"]


def claude_agent(sdk, reserve=lambda *args: 16000, snapshot=None, **kwargs):
    agent = claude.ClaudeAgent(snapshot or claude_snapshot(), [], client=sdk, **kwargs)
    if hasattr(agent, "use_budget"):
        agent.use_budget(reserve)
    return agent


class StatusError(Exception):
    def __init__(self, status_code):
        super().__init__(status_code)
        self.status_code = status_code


class CostCapTests(unittest.TestCase):
    def test_each_request_is_reserved_first_and_uses_the_granted_output_limit(self):
        sdk = FakeSdk([reply("max_tokens"), reply()])

        def reserve(turn, attempt, input_tokens, max_output):
            sdk.log.append(("reserve", attempt))
            self.assertGreater(input_tokens, 1000)  # counted tokens plus a margin
            return 2000

        claude_agent(sdk, reserve).next_action(Observation("o", [], 1, None, None))
        self.assertEqual([name for name, _ in sdk.log], ["count", "reserve", "create", "reserve", "create"])
        self.assertEqual([request["max_tokens"] for request in sdk.creates], [2000, 2000])

    def test_no_request_is_sent_without_a_reservation(self):
        sdk = FakeSdk([reply()])
        agent = claude.ClaudeAgent(claude_snapshot(), [], client=sdk)
        with self.assertRaises(Exception):
            agent.next_action(Observation("o", [], 1, None, None))
        self.assertEqual(sdk.creates, [])

    def test_sdk_does_not_retry_behind_the_reservation(self):
        seen = {}
        fake_module = SimpleNamespace(Anthropic=lambda **kwargs: seen.update(kwargs))
        with mock.patch.dict(sys.modules, {"anthropic": fake_module}):
            claude.ClaudeAgent._default_client()
        self.assertEqual(seen["max_retries"], 0)

    def test_request_with_unknown_billing_is_not_retried(self):
        for error in (TimeoutError(), StatusError(504), StatusError(408)):
            with self.subTest(error=repr(error)):
                sdk = FakeSdk([error, reply()])
                with self.assertRaises(claude.ModelApiError) as raised:
                    claude_agent(sdk, sleep=lambda _: None).next_action(Observation("o", [], 1, None, None))
                self.assertIsNone(raised.exception.usage)  # the reservation stays charged
                self.assertEqual(len(sdk.creates), 1)

    def test_usage_of_the_first_attempt_survives_a_failed_second_attempt(self):
        sdk = FakeSdk([reply("max_tokens"), TimeoutError()])
        with self.assertRaises(claude.ModelApiError) as raised:
            claude_agent(sdk).next_action(Observation("o", [], 1, None, None))
        usage = raised.exception.usage
        self.assertEqual((usage["input"], usage["output"], usage["settled"]), (1000, 50, 1))

    def test_rejected_request_is_retried_under_the_same_reservation(self):
        sdk, reservations = FakeSdk([StatusError(529), reply()]), []

        def reserve(*args):
            reservations.append(args)
            return 16000

        claude_agent(sdk, reserve, sleep=lambda _: None).next_action(Observation("o", [], 1, None, None))
        self.assertEqual((len(sdk.creates), len(reservations)), (2, 1))


class SessionTests(unittest.TestCase):
    def test_session_is_renewed_during_a_long_model_call(self):
        api = FakeApi()

        class Slow:
            def next_action(self, observation):
                time.sleep(0.2)
                return Decision("finish", {"summary": "done"})

        with mock.patch.object(worker, "SESSION_RENEW_SECONDS", 0.02, create=True):
            worker.Worker(api, lambda *args: Slow()).run_once()
        self.assertGreater(api.heartbeats, 3)

    def test_model_call_is_bounded_by_the_remaining_wall_time(self):
        sdk = FakeSdk([reply()])
        claude_agent(sdk, snapshot=claude_snapshot(maxWallTimeSeconds=45), clock=lambda: 0.0).next_action(
            Observation("o", [], 1, None, None))
        self.assertEqual(sdk.creates[0].get("timeout"), 45)


class FakeApi:
    def __init__(self, claim_error=None, tool_error=None):
        self.session_token = None
        self.claim_error, self.tool_error = claim_error, tool_error
        self.finished = None
        self.heartbeats = 0

    def claim(self):
        if self.claim_error:
            raise self.claim_error
        return {"runId": "r", "sessionToken": "s", "configSnapshot": SNAPSHOT}

    def initialize(self):
        return {}

    def heartbeat(self):
        self.heartbeats += 1
        return {"status": "RUNNING"}

    def model_invocation(self, *args, **kwargs):
        return {"status": "ok"}

    def tool_call(self, *args):
        if self.tool_error:
            raise self.tool_error
        return {"status": "ok"}

    def finish(self, outcome, reason):
        self.finished = (outcome, reason)


class ContainmentTests(unittest.TestCase):
    def test_rejected_tool_call_fails_the_run_instead_of_the_process(self):
        api = FakeApi(tool_error=ApiError(413, None))
        agent = SimpleNamespace(next_action=lambda observation: Decision("get_balance", {}))
        self.assertEqual(worker.Worker(api, lambda *args: agent).run_once().outcome, "failed")
        self.assertEqual(api.finished[0], "failed")

    def test_agent_construction_error_fails_the_run(self):
        def broken(*args):
            raise RuntimeError("no credentials: do not store this text")

        api = FakeApi()
        self.assertEqual(worker.Worker(api, broken).run_once().outcome, "failed")
        self.assertNotIn("do not store", api.finished[1])

    def test_main_survives_an_unreachable_api_and_reports_a_bad_credential(self):
        for error, code in ((Unreachable(), 1), (ApiError(404, None), 1), (SessionEnded(), 2)):
            with self.subTest(error=repr(error)), mock.patch.dict(os.environ, {"FINMIND_WORKER_TOKEN": "x" * 32}), \
                    mock.patch.object(worker, "Client", lambda *args: FakeApi(claim_error=error)), \
                    contextlib.redirect_stderr(None):
                self.assertEqual(worker.main(["--once"]), code)

    def test_oversized_calls_are_sent_as_recordable_malformed_attempts(self):
        sent = []

        def transport(method, url, headers, body, timeout):
            sent.append(json.loads(body))
            self.assertLessEqual(len(body), 16_384)
            return 200, b'{"status": "malformed"}'

        client = Client("http://api", "w" * 32, transport)
        client.session_token = "s"
        client.tool_call("turn-0001", "submit_plan", {"text": "x" * 40_000})
        client.tool_call("turn-0002", "n" * 300, {})
        self.assertGreater(sent[0]["arguments"]["_oversized_arguments_bytes"], 16_384)
        self.assertLessEqual(len(sent[1]["tool"]), 100)

    def test_long_output_text_cannot_lose_the_usage_record(self):
        sent = []
        client = Client("http://api", "w" * 32, lambda *args: (sent.append(json.loads(args[3])), (200, b"{}"))[1])
        client.session_token = "s"
        client.model_invocation(1, 10, 10, 1, "x" * 150_000)
        self.assertLess(len(sent[0]["outputText"]), 100_000)

    def test_non_json_error_body_is_an_api_error(self):
        client = Client("http://api", "w" * 32, lambda *args: (413, b"<html>too large</html>"))
        with self.assertRaises(ApiError):
            client.claim()


API = os.environ.get("FINMIND_E2E_API_URL")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN")
class ServerBudgetTests(unittest.TestCase):
    """Against the real API and database; no model is ever called."""

    def claimed_run(self, cap=500_000):
        from test_end_to_end import config, http
        body = config()
        body.update({"maxModelTokens": 300_000, "maxCostMicroUsd": cap,
                     "agent": {"provider": "anthropic", "modelId": "claude-opus-5-5", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        client = Client(API, TOKEN)
        for _ in range(1000):
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                client.initialize()
                return client, run_id
            client.finish("failed", "test: cleared from the shared test queue")
        self.fail("run was not claimed")

    def test_initialize_retry_succeeds(self):
        client, _ = self.claimed_run()
        self.assertEqual(client.initialize()["status"], "RUNNING")
        client.finish("completed", "test")

    def test_two_worst_case_attempts_cannot_exceed_the_cap(self):
        client, _ = self.claimed_run()
        first = client.reserve_model_call(1, 1, 2000, 16000)
        second = client.reserve_model_call(1, 2, 2000, 16000)
        self.assertEqual(first["data"]["max_output_tokens"], 16000)
        self.assertLess(second["data"]["max_output_tokens"], 16000)
        self.assertLessEqual(second["data"]["committed_micro_usd"], 500_000)
        self.assertEqual(client.reserve_model_call(1, 2, 2000, 16000)["data"], second["data"])  # lost response
        client.finish("completed", "test")

    def test_unanswered_attempt_stays_reserved(self):
        client, _ = self.claimed_run(cap=2_000_000)
        client.reserve_model_call(1, 1, 2000, 16000)  # worst case 330,000
        client.reserve_model_call(1, 2, 2000, 16000)
        client.model_invocation(1, 1000, 100, 5, "", settledAttempts=1)  # 6,000; attempt 2 was never answered
        after = client.reserve_model_call(2, 1, 2000, 16000)
        self.assertEqual(after["data"]["committed_micro_usd"], 6000 + 330_000 + 330_000)
        client.finish("completed", "test")

    def test_oversized_attempt_is_recorded_as_malformed(self):
        from test_end_to_end import events
        client, run_id = self.claimed_run()
        self.assertEqual(client.tool_call("turn-0001", "get_balance", {"text": "x" * 40_000})["status"], "malformed")
        self.assertIn("TOOL_CALL_PROPOSED", [e["eventType"] for e in events(run_id)])
        client.finish("completed", "test")

    def test_usage_is_recorded_after_cancellation(self):
        from test_end_to_end import events, http
        client, run_id = self.claimed_run()
        client.reserve_model_call(1, 1, 2000, 16000)
        http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test"})
        response = client.model_invocation(1, 1000, 100, 5, "late")
        self.assertEqual(response["reason_code"], "RUN_NOT_ACTIVE")
        recorded = [e for e in events(run_id) if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        self.assertEqual([e["payload"]["costMicroUsd"] for e in recorded], [6000])
        with self.assertRaises(SessionEnded):
            client.tool_call("turn-0001", "get_balance", {})  # a revoked token reports usage and nothing else


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN")
class WorkerBudgetTests(unittest.TestCase):
    """The two reported defects through the real worker, API and database, with a fake SDK."""

    def drive(self, create):
        from test_end_to_end import config, events, http, run_until_terminal, test_worker
        while test_worker().run_once() is not None:  # leave only this test's run in the queue
            pass
        body = config()
        body.update({"maxModelTokens": 300_000, "maxCostMicroUsd": 500_000,
                     "agent": {"provider": "anthropic", "modelId": "claude-opus-5-5", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        sdk = SimpleNamespace(messages=SimpleNamespace(
            count_tokens=lambda **kwargs: SimpleNamespace(input_tokens=1000),
            create=lambda **kwargs: create(run_id, kwargs)))
        factory = lambda snapshot, tools: claude.ClaudeAgent(snapshot, tools, client=sdk)
        run_until_terminal(run_id, worker.Worker(Client(API, TOKEN), agent_factory=factory))
        return [e["payload"] for e in events(run_id) if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]

    def test_two_full_attempts_stay_under_the_cap(self):
        def create(run_id, request):
            answer = reply("max_tokens")
            answer.usage.output_tokens = request["max_tokens"]  # the model uses everything it was allowed
            return answer

        recorded = self.drive(create)
        self.assertEqual(len(recorded), 1)
        self.assertGreater(recorded[0]["costMicroUsd"], 320_000)  # both attempts are counted
        self.assertLessEqual(recorded[0]["costMicroUsd"], 500_000)

    def test_cancel_during_a_model_call_still_records_its_cost(self):
        def create(run_id, request):
            from test_end_to_end import http
            http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test"})
            return reply()

        self.assertEqual([p["costMicroUsd"] for p in self.drive(create)], [5000])


if __name__ == "__main__":
    unittest.main()
