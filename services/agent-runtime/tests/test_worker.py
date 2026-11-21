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
        self.finished = None

    def claim(self):
        return {"runId": "run-1", "sessionToken": "s", "configSnapshot": SNAPSHOT, "tools": []}

    def initialize(self):
        if self.initialize_error:
            raise self.initialize_error
        return {"status": "RUNNING"}

    def heartbeat(self):
        return {"status": self.statuses.pop(0) if self.statuses else "RUNNING"}

    def model_invocation(self, turn, *_):
        return {"status": "ok"}

    def tool_call(self, call_id, tool, arguments):
        self.calls.append((call_id, tool))
        return {"status": "denied", "reason_code": self.tool_reason} if self.tool_reason else {"status": "ok", "data": {}}

    def finish(self, outcome, reason):
        self.finished = (outcome, reason)
        return {}


class ScriptedAgent:
    def __init__(self, tools):
        self.tools = list(tools)

    def next_action(self, observation):
        return Decision(self.tools.pop(0), {}, "scripted")


class WorkerTests(unittest.TestCase):
    def run_worker(self, client, tools, clock=None):
        kwargs = {"clock": clock} if clock else {}
        return Worker(client, lambda snapshot: ScriptedAgent(tools), **kwargs).run_once()

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

    def test_local_wall_time_stops_the_run(self):
        ticks = iter([0, 0, 601])
        client = FakeClient()
        result = self.run_worker(client, ["get_balance"] * 5, clock=lambda: next(ticks))
        self.assertEqual(result.outcome, "budget_exceeded")
        self.assertEqual(client.finished[0], "budget_exceeded")


if __name__ == "__main__":
    unittest.main()
