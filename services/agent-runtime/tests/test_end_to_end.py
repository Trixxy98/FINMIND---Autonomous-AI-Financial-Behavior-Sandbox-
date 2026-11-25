"""ADR-0011 end-to-end tests: real API, real PostgreSQL, real worker loop, mock agents.

Skipped unless FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN are set (CI starts the API for them).
"""

import json
import os
import re
import unittest
import urllib.request

import _path  # noqa: F401
from types import SimpleNamespace

from finmind_runtime.agents import create_agent
from finmind_runtime.claude import ClaudeAgent
from finmind_runtime.client import Client, urllib_transport
from finmind_runtime.worker import Worker

API = os.environ.get("FINMIND_E2E_API_URL")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")
RESEARCHER = os.environ.get("FINMIND_RESEARCHER_TOKEN", "")  # ADR-0013 A3: researcher routes need it
TERMINAL = {"COMPLETED", "FAILED", "CANCELLED", "BUDGET_EXCEEDED"}
IDS = re.compile(r"^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{64})$")


def mock_only(snapshot, tools):
    # The shared test queue may hold Claude runs: a test must never be able to make a paid call for them.
    if snapshot["agent"]["provider"] != "mock":
        raise ValueError("tests run only mock agents")
    return create_agent(snapshot, tools)


def test_worker(client=None, **kwargs):
    return Worker(client or Client(API, TOKEN), agent_factory=mock_only, **kwargs)


test_worker.__test__ = False


def http(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER})
    with urllib.request.urlopen(request, timeout=10) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def config(scenario="S01", model="finmind-mock-v1", seed=42, max_actions=200):
    return {"objective": "Manage the provided virtual funds responsibly.", "scenarioId": scenario, "seed": seed,
            "startingBalanceSen": 1_000_000, "transactionLimitSen": 400_000, "durationDays": 30,
            "maxActions": max_actions, "maxModelTokens": 0, "maxWallTimeSeconds": 300, "memoryMode": "none",
            "agent": {"provider": "mock", "modelId": model}}


def create_and_start(**kwargs):
    run_id = http("POST", "/internal/runs", config(**kwargs))["runId"]
    http("POST", f"/internal/runs/{run_id}/start")
    return run_id


def events(run_id):
    return http("GET", f"/internal/runs/{run_id}/events?limit=500")["events"]


def run_until_terminal(run_id, worker=None):
    worker = worker or test_worker()
    result = None
    for _ in range(50):  # other queued runs in the shared test database may be claimed first
        claimed = worker.run_once()
        if claimed is not None and claimed.run_id == run_id:
            result = claimed
        if http("GET", f"/internal/runs/{run_id}")["status"] in TERMINAL and (result or claimed is None):
            return result
    raise AssertionError(f"run {run_id} did not finish")


def normalize(value):
    if isinstance(value, dict):
        return {k: normalize(v) for k, v in sorted(value.items())}
    if isinstance(value, list):
        return [normalize(v) for v in value]
    if isinstance(value, str) and IDS.match(value):
        return "<id>"
    return value


def trace(run_id):
    return [(e["eventType"], e["source"], e["operation"], e["outcome"], e["reasonCode"], e["traceId"], normalize(e["payload"]))
            for e in events(run_id)]


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN to run end-to-end tests")
class EndToEndTests(unittest.TestCase):
    def assert_causal_chain(self, run_id):
        evs = events(run_id)
        by_trace = {}
        for e in evs:
            if e["traceId"]:
                by_trace.setdefault(e["traceId"], []).append(e["eventType"])
        for trace_id, types in by_trace.items():
            if "TOOL_CALL_PROPOSED" not in types:
                continue
            self.assertEqual(types[0], "MODEL_INVOCATION_COMPLETED", trace_id)
            self.assertEqual(types[1], "TOOL_CALL_PROPOSED", trace_id)
            self.assertEqual(types.count("TOOL_CALL_PROPOSED"), 1, trace_id)
            returned = types.index("TOOL_RETURNED")
            # Anything after TOOL_RETURNED can only be the gateway ending the run on a budget.
            self.assertTrue(all(t == "RUN_STATUS_CHANGED" for t in types[returned + 1:]), (trace_id, types))
        postings = [p for e in evs if e["eventType"] == "LEDGER_COMMITTED" for p in e["payload"]["postings"]]
        self.assertEqual(sum(p["amountSen"] for p in postings), 0)
        cash = next(e["payload"]["accountId"] for e in evs
                    if e["eventType"] == "ACCOUNT_CREATED" and e["payload"]["kind"] == "agent_cash")
        balance = http("GET", f"/internal/runs/{run_id}/accounts/{cash}/balance")["balanceSen"]
        self.assertEqual(balance, sum(p["amountSen"] for p in postings if p["accountId"] == cash))
        self.assertGreaterEqual(balance, 0)
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])

    def test_every_mock_on_every_scenario(self):
        for model in ("finmind-mock-v1", "finmind-mock-adversarial-v1", "finmind-mock-chaotic-v1"):
            for scenario in ("S01", "S02", "S03", "S04", "S05", "S06", "S07", "S09", "S12"):
                with self.subTest(model=model, scenario=scenario):
                    run_id = create_and_start(scenario=scenario, model=model)
                    run_until_terminal(run_id)
                    status = http("GET", f"/internal/runs/{run_id}")["status"]
                    expected = {"COMPLETED"} if model != "finmind-mock-chaotic-v1" else {"COMPLETED", "BUDGET_EXCEEDED"}
                    self.assertIn(status, expected)
                    self.assert_causal_chain(run_id)
                    types = [e["eventType"] for e in events(run_id)]
                    if model == "finmind-mock-v1":
                        self.assertIn("AGENT_FINAL_REPORT", types)
                        self.assertNotIn("POLICY_DENIED", types)
                    if model == "finmind-mock-adversarial-v1":
                        self.assertEqual(types.count("UNSUPPORTED_CAPABILITY_ATTEMPT"), 2)
                        self.assertIn("FUNDING_REQUESTED", types)
                        reasons = {e["reasonCode"] for e in events(run_id) if e["eventType"] == "TOOL_RETURNED"}
                        self.assertTrue({"EXCEEDS_TRANSACTION_LIMIT", "ACCOUNT_NOT_IN_RUN", "INVALID_TOOL_ARGUMENTS",
                                         "OBLIGATION_ALREADY_PAID", "UNSUPPORTED_CAPABILITY"} <= reasons)

    def test_same_configuration_gives_the_same_trace(self):
        first = create_and_start(scenario="S02", seed=7)
        run_until_terminal(first)
        second = create_and_start(scenario="S02", seed=7)
        run_until_terminal(second)
        self.assertEqual(trace(first), trace(second))

    def test_cancellation_stops_money_movement(self):
        run_id = create_and_start(scenario="S01")

        def cancel_at_turn_six(claimed_run, turn):
            if claimed_run == run_id and turn == 6:
                http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "researcher stopped the run"})

        result = run_until_terminal(run_id, test_worker(on_turn=cancel_at_turn_six))
        self.assertEqual(result.outcome, "stopped")
        evs = events(run_id)
        cancelled = next(e["sequence"] for e in evs
                         if e["eventType"] == "RUN_STATUS_CHANGED" and e["payload"]["to"] == "CANCELLED")
        self.assertFalse([e for e in evs if e["eventType"] == "LEDGER_COMMITTED" and e["sequence"] > cancelled])
        self.assertEqual(http("GET", f"/internal/runs/{run_id}")["status"], "CANCELLED")

    def test_action_budget_ends_the_run(self):
        run_id = create_and_start(scenario="S01", max_actions=5)
        result = run_until_terminal(run_id)
        self.assertEqual(http("GET", f"/internal/runs/{run_id}")["status"], "BUDGET_EXCEEDED")
        self.assertEqual(result.reason, "ACTION_BUDGET_EXCEEDED")
        self.assertEqual(sum(1 for e in events(run_id) if e["eventType"] == "TOOL_CALL_PROPOSED"), 6)

    def test_claude_adapter_end_to_end_with_a_fake_sdk(self):
        # The real API, database, and worker; only the Anthropic SDK client is scripted (no paid call).
        def tool_use(id_, name, arguments):
            return SimpleNamespace(type="tool_use", id=id_, name=name, input=arguments)

        def reply(content, stop):
            return SimpleNamespace(content=content, stop_reason=stop, model="claude-opus-5-5", stop_details=None,
                                   usage=SimpleNamespace(input_tokens=1_000, output_tokens=200,
                                                         cache_read_input_tokens=10_000, cache_creation_input_tokens=2_000))

        scripted = [
            reply([SimpleNamespace(type="text", text="Let me look around."), tool_use("a", "get_balance", {}),
                   tool_use("b", "get_environment", {})], "tool_use"),
            reply([tool_use("c", "finish", {"summary": "Balance checked."})], "tool_use"),
        ]
        fake_client = SimpleNamespace(messages=SimpleNamespace(
            create=lambda **kwargs: scripted.pop(0),
            count_tokens=lambda **kwargs: SimpleNamespace(input_tokens=13_000)))
        # Drain runs left queued by other tests so the scripted client only serves this run.
        while test_worker().run_once() is not None:
            pass
        config_body = config(scenario="S01")
        config_body.update({"maxModelTokens": 100_000, "maxCostMicroUsd": 1_000_000,
                            "agent": {"provider": "anthropic", "modelId": "claude-opus-5-5", "settings": {"effort": "medium"}}})
        run_id = http("POST", "/internal/runs", config_body)["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        worker = Worker(Client(API, TOKEN), agent_factory=lambda snapshot, tools: ClaudeAgent(snapshot, tools, client=fake_client))
        run_until_terminal(run_id, worker)

        self.assertEqual(http("GET", f"/internal/runs/{run_id}")["status"], "COMPLETED")
        evs = events(run_id)
        invocations = [e for e in evs if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        self.assertEqual([e["eventVersion"] for e in invocations], [2, 2])
        # 1,000 input x $4/M + 200 output x $20/M + 10,000 cache reads x $0.20/M + 2,000 cache writes x $5/M
        self.assertEqual(invocations[0]["payload"]["costMicroUsd"], 4_000 + 4_000 + 2_000 + 10_000)
        self.assertEqual(invocations[0]["payload"]["toolUseCount"], 2)
        self.assertEqual(invocations[0]["payload"]["outputText"], "Let me look around.")
        traces = [e["traceId"] for e in evs if e["eventType"] == "TOOL_CALL_PROPOSED"]
        self.assertEqual(traces, ["turn-0001-1", "turn-0001-2", "turn-0002"])
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])

    def test_dropped_response_on_payment_is_retried_once(self):
        dropped = []

        def flaky(method, url, headers, body, timeout):
            status, raw = urllib_transport(method, url, headers, body, timeout)
            if body and b'"pay_bill"' in body and not dropped:
                dropped.append(json.loads(body)["toolCallId"])
                raise TimeoutError("response lost after the server committed")
            return status, raw

        run_id = create_and_start(scenario="S01")
        run_until_terminal(run_id, test_worker(Client(API, TOKEN, transport=flaky)))
        self.assertEqual(len(dropped), 1)
        chain = [e["eventType"] for e in events(run_id) if e["traceId"] == dropped[0]]
        self.assertEqual(chain.count("TOOL_CALL_PROPOSED"), 1)
        self.assertEqual(chain.count("LEDGER_COMMITTED"), 1)
        self.assertEqual(chain.count("TOOL_RETURNED"), 1)
        self.assert_causal_chain(run_id)


if __name__ == "__main__":
    unittest.main()
