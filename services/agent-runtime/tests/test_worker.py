import unittest

import _path  # noqa: F401
from finmind_runtime.agents import Decision
from finmind_runtime.client import ApiError
from finmind_runtime.worker import Worker

SNAPSHOT = {"objective": "o", "maxWallTimeSeconds": 600, "maxActions": 50, "seed": 1, "scenarioVersion": "S01/v1",
            "agent": {"provider": "mock", "modelId": "finmind-mock-v1"}}


class FakeClient:
    def __init__(self, statuses=None, tool_reason=None, initialize_error=None):
        self.session_token = None
        self.statuses = list(statuses or [])
        self.tool_reason = tool_reason
        self.initialize_error = initialize_error
        self.calls = []
        self.invocations = []
        self.finished = None

    def claim(self):
        return {"runId": "run-1", "sessionToken": "s", "configSnapshot": SNAPSHOT, "tools": []}

    def initialize(self):
        if self.initialize_error:
            raise self.initialize_error
        return {"status": "RUNNING"}

    def heartbeat(self):
        return {"status": self.statuses.pop(0) if self.statuses else "RUNNING"}

    def model_invocation(self, turn, *_, **extra):
        self.invocations.append((turn, extra))
        return self.invocation_response

    invocation_response = {"status": "ok"}

    def tool_call(self, call_id, tool, arguments):
        self.calls.append((call_id, tool))
        return {"status": "denied", "reason_code": self.tool_reason} if self.tool_reason else {"status": "ok", "data": {}}

    def finish(self, outcome, reason):
        self.finished = (outcome, reason)
        return {}


class DecisionAgent:
    """Returns prepared Decision objects (provider-style turns)."""

    def __init__(self, decisions):
        self.decisions = list(decisions)
        self.observations = []

    def next_action(self, observation):
        self.observations.append(observation)
        return self.decisions.pop(0)


class ScriptedAgent:
    def __init__(self, tools):
        self.tools = list(tools)

    def next_action(self, observation):
        return Decision(self.tools.pop(0), {}, "scripted")


class WorkerTests(unittest.TestCase):
    def run_worker(self, client, tools, clock=None):
        kwargs = {"clock": clock} if clock else {}
        return Worker(client, lambda snapshot, tools_: ScriptedAgent(tools), **kwargs).run_once()

    def test_finish_completes_the_run_with_turn_ids(self):
        client = FakeClient()
        result = self.run_worker(client, ["get_balance", "finish"])
        self.assertEqual((result.outcome, result.turns), ("completed", 2))
        self.assertEqual(client.calls, [("turn-0001", "get_balance"), ("turn-0002", "finish")])
        self.assertEqual(client.finished[0], "completed")
        self.assertIsNone(client.session_token)

    def test_cancelled_run_stops_without_finishing(self):
        client = FakeClient(statuses=["RUNNING", "CANCELLED"])
        result = self.run_worker(client, ["get_balance"] * 5)
        self.assertEqual((result.outcome, result.turns), ("stopped", 1))
        self.assertIsNone(client.finished)

    def test_server_budget_stops_the_run(self):
        result = self.run_worker(FakeClient(tool_reason="ACTION_BUDGET_EXCEEDED"), ["get_balance"] * 5)
        self.assertEqual((result.outcome, result.reason), ("stopped", "ACTION_BUDGET_EXCEEDED"))

    def test_rejected_initialization_fails_the_run(self):
        client = FakeClient(initialize_error=ApiError(400, {"reasonCode": "INVALID_RUN_CONFIG"}))
        result = self.run_worker(client, [])
        self.assertEqual(client.finished, ("failed", "runtime: initialization rejected (INVALID_RUN_CONFIG)"))
        self.assertEqual(result.outcome, "failed")

    def test_several_calls_in_one_turn_get_numbered_ids_and_results(self):
        client = FakeClient()
        agent = DecisionAgent([
            Decision(calls=[("get_balance", {}), ("list_obligations", {})], model="claude-opus-5-5",
                     stop_reason="tool_use", input_tokens=10, cache_read_tokens=5),
            Decision(calls=[("finish", {"summary": "done"})], stop_reason="tool_use"),
        ])
        result = Worker(client, lambda snapshot, tools: agent).run_once()
        self.assertEqual(result.outcome, "completed")
        self.assertEqual(client.calls, [("turn-0001-1", "get_balance"), ("turn-0001-2", "list_obligations"), ("turn-0002", "finish")])
        self.assertEqual([tool for tool, _ in agent.observations[1].results], ["get_balance", "list_obligations"])
        self.assertEqual(client.invocations[0][1]["toolUseCount"], 2)
        self.assertEqual(client.invocations[0][1]["cacheReadInputTokens"], 5)

    def test_turns_without_tool_calls_end_the_run_after_three(self):
        client = FakeClient()
        result = Worker(client, lambda snapshot, tools: DecisionAgent([Decision(calls=[])] * 3)).run_once()
        self.assertEqual((result.outcome, result.turns), ("completed", 3))
        self.assertEqual(client.finished, ("completed", "agent stopped calling tools"))
        self.assertEqual(client.calls, [])

    def test_refusal_is_recorded_and_fails_the_run(self):
        client = FakeClient()
        agent = DecisionAgent([Decision(calls=[], terminal="refusal", refusal_category="cyber", stop_reason="refusal")])
        result = Worker(client, lambda snapshot, tools: agent).run_once()
        self.assertEqual(client.finished, ("failed", "model refusal (cyber)"))
        self.assertEqual(client.invocations[0][1]["refusalCategory"], "cyber")
        self.assertEqual(result.outcome, "failed")

    def test_rejected_invocation_record_fails_the_run(self):
        client = FakeClient()
        client.invocation_response = {"status": "malformed", "reason_code": "INVALID_TOOL_ARGUMENTS"}
        result = self.run_worker(client, ["get_balance"])
        self.assertEqual(client.finished, ("failed", "runtime: invocation record rejected (INVALID_TOOL_ARGUMENTS)"))
        self.assertEqual((result.outcome, client.calls), ("failed", []))

    def test_local_wall_time_stops_the_run(self):
        ticks = iter([0, 0, 601])
        client = FakeClient()
        result = self.run_worker(client, ["get_balance"] * 5, clock=lambda: next(ticks))
        self.assertEqual(result.outcome, "budget_exceeded")
        self.assertEqual(client.finished[0], "budget_exceeded")


if __name__ == "__main__":
    unittest.main()
