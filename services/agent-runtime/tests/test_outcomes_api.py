"""ADR-0018 step 2: outcomes over HTTP and in the export, against the real API with mock and scripted agents."""

import json
import unittest

import _path  # noqa: F401
from finmind_runtime.agents import create_agent
from finmind_runtime.client import Client
from finmind_runtime.worker import Worker
from test_end_to_end import API, RESEARCHER, TOKEN, config, http, test_worker
from test_injection import SEED, ObedientAgent
from test_operator_api import send


def outcome(body, outcome_id):
    return next(o for o in body["outcomes"] if o["id"] == outcome_id)


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class OutcomesApiTests(unittest.TestCase):
    def run_to_end(self, run_id, factory=None):
        worker = Worker(Client(API, TOKEN), agent_factory=factory) if factory else test_worker()
        for _ in range(200):
            result = worker.run_once()
            if result is None or result.run_id == run_id:
                return

    def test_the_prudent_mock_pays_every_s01_bill_on_time(self):
        run_id = http("POST", "/api/v1/runs", config(scenario="S01"))["runId"]
        http("POST", f"/api/v1/runs/{run_id}/start")
        self.run_to_end(run_id)
        body = http("GET", f"/api/v1/runs/{run_id}/outcomes")
        self.assertEqual((body["outcomesVersion"], body["scenarioVersion"], body["runId"]), ("finmind-outcomes/v1", "S01/v1", run_id))
        paid = outcome(body, "bills_paid_by_due")
        self.assertEqual((paid["value"], paid["denominator"], paid["exposed"]), (3, 3, True))
        self.assertEqual(outcome(body, "money_outside_bills")["value"], 0)

    def test_an_agent_that_obeys_the_s07_message_shows_in_the_outcomes_and_the_export(self):
        run_id = http("POST", "/internal/runs", config(scenario="S07", seed=SEED, max_actions=20))["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        obedient = ObedientAgent()

        def factory(snapshot, tools):
            if snapshot["agent"]["provider"] != "mock":
                raise ValueError("tests run only mock agents")
            return obedient if snapshot["seed"] == SEED and snapshot["scenarioId"] == "S07" else create_agent(snapshot, tools)
        self.run_to_end(run_id, factory)

        body = http("GET", f"/api/v1/runs/{run_id}/outcomes")
        above = outcome(body, "S07.attempts_above_limit_after_message")
        self.assertEqual((above["exposed"], above["value"], len(above["evidence"])), (True, 1, 1))
        self.assertEqual(outcome(body, "S07.money_to_named_merchant_outside_bills")["value"], 0)  # the transfer was refused

        status, raw = send("GET", f"/api/v1/runs/{run_id}/export?format=jsonl")
        records = [json.loads(line) for line in raw.decode().splitlines()]
        header = records[0]
        self.assertEqual((header["exportSchemaVersion"], header["outcomesVersion"]), ("finmind-export/v2", "finmind-outcomes/v1"))
        exported = [r for r in records if r["type"] == "outcomes"]
        self.assertEqual(len(exported), 1)
        self.assertEqual(exported[0]["outcomes"], body["outcomes"])
        events = {r["sequence"]: r["eventType"] for r in records if r["type"] == "event"}
        self.assertEqual(events[above["evidence"][0]], "TOOL_RETURNED")  # evidence points at real events

    def test_a_comparison_reports_where_each_outcome_happened_over_exposed_runs(self):
        from test_batches_api import call, finish
        while test_worker().run_once() is not None:
            pass
        prudent = call("POST", "/batches", {"label": "S04 prudent", "config": config(scenario="S04"), "count": 2, "start": True})[1]
        adversarial = call("POST", "/batches", {"label": "S04 adversarial", "count": 2, "start": True,
                                                "config": config(scenario="S04", model="finmind-mock-adversarial-v1")})[1]
        finish(prudent)
        finish(adversarial)
        status, comparison = call("GET", f"/compare?batch={prudent['batchId']}&batch={adversarial['batchId']}")
        self.assertEqual((status, comparison["comparisonVersion"], comparison["outcomesVersion"]),
                         (200, "finmind-compare/v2", "finmind-outcomes/v1"))
        by_id = {o["id"]: o for o in comparison["outcomes"]}
        repeats = by_id["repeat_payment_attempts"]["groups"]
        self.assertEqual([(g["exposed"], g["happenedIn"]) for g in repeats], [(2, 0), (2, 2)])
        offer = by_id["S04.money_to_offer_sender"]["groups"]
        self.assertEqual([(g["exposed"], g["happenedIn"], g["notExposed"]) for g in offer], [(2, 0, []), (2, 0, [])])
        paid = by_id["bills_paid_by_due"]["groups"]
        self.assertEqual([g["completeIn"] for g in paid], [2, 2])
        self.assertNotIn("score", json.dumps(comparison["outcomes"]))

    def test_unknown_runs_and_missing_credentials(self):
        self.assertEqual(send("GET", "/api/v1/runs/00000000-0000-4000-8000-000000000001/outcomes")[0], 404)
        run_id = http("POST", "/api/v1/runs", config(scenario="S01"))["runId"]
        self.assertEqual(send("GET", f"/api/v1/runs/{run_id}/outcomes", token=TOKEN)[0], 401)
        draft = http("GET", f"/api/v1/runs/{run_id}/outcomes")  # a run that never started: nothing was exposed
        self.assertTrue(all(o["exposed"] is False and o["value"] is None for o in draft["outcomes"]))


if __name__ == "__main__":
    unittest.main()
