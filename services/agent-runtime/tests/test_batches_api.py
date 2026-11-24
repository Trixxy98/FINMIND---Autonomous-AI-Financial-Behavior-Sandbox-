"""ADR-0015 step 2: batches and comparison, against the real API and database."""

import json
import unittest
import urllib.error
import urllib.request

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, config, http, run_until_terminal, test_worker


def call(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + "/api/v1" + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        with error:
            return error.code, json.loads(error.read() or b"null")


def finish(batch):
    for run_id in batch["runIds"]:
        run_until_terminal(run_id)


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class BatchApiTests(unittest.TestCase):
    def setUp(self):
        while test_worker().run_once() is not None:  # start from an empty queue
            pass

    def test_replications_are_identical_configurations_started_in_a_recorded_order(self):
        status, batch = call("POST", "/batches", {"label": "S01 replications", "config": config(scenario="S01"), "count": 3, "start": True})
        self.assertEqual(status, 201)
        self.assertEqual(len(batch["runIds"]), 3)
        detail = call("GET", f"/batches/{batch['batchId']}")[1]
        self.assertEqual((detail["label"], detail["count"], detail["seedMode"]), ("S01 replications", 3, "same"))
        self.assertEqual(sorted(detail["startOrder"]), [0, 1, 2])
        runs = {r["runId"]: r for r in detail["runs"]}
        hashes = {http("GET", f"/internal/runs/{run_id}")["configHash"] for run_id in runs}
        self.assertEqual(len(hashes), 1)  # same configuration and seed
        queued = sorted(runs.values(), key=lambda r: r["queuedAt"])
        self.assertEqual([r["batchPosition"] for r in queued], detail["startOrder"])
        self.assertEqual(batch["runIds"], [r["runId"] for r in queued])

    def test_seed_list_is_opt_in_and_must_match_the_count(self):
        status, batch = call("POST", "/batches", {"config": config(), "count": 2, "seeds": [7, 8], "start": False})
        self.assertEqual(status, 201)
        seeds = sorted(http("GET", f"/internal/runs/{r}")["configSnapshot"]["seed"] for r in batch["runIds"])
        self.assertEqual(seeds, [7, 8])
        self.assertEqual(call("POST", "/batches", {"config": config(), "count": 2, "seeds": [7]})[0], 400)

    def test_invalid_requests_create_nothing(self):
        before = len(call("GET", "/batches?limit=500")[1]["batches"])
        bad = config()
        bad["seed"] = -1
        for body in ({"config": bad, "count": 2}, {"config": config(), "count": 0}, {"config": config(), "count": 21},
                     {"config": config(), "count": 2, "seeds": [1, -1]}, {"count": 2}):
            with self.subTest(body=str(body)[:60]):
                self.assertEqual(call("POST", "/batches", body)[0], 400)
        self.assertEqual(len(call("GET", "/batches?limit=500")[1]["batches"]), before)

    def test_comparison_names_the_factor_and_summarizes_each_metric(self):
        _, s01 = call("POST", "/batches", {"label": "S01", "config": config(scenario="S01"), "count": 3, "start": True})
        _, s03 = call("POST", "/batches", {"label": "S03", "config": config(scenario="S03"), "count": 3, "start": True})
        finish(s01)
        finish(s03)
        status, comparison = call("GET", f"/compare?batch={s01['batchId']}&batch={s03['batchId']}")
        self.assertEqual(status, 200)
        self.assertEqual(comparison["comparisonVersion"], "finmind-compare/v1")
        self.assertEqual(comparison["metricsVersion"], "finmind-metrics/v1")
        self.assertEqual(comparison["configuration"]["betweenGroups"], ["scenarioId"])
        self.assertEqual(comparison["configuration"]["withinGroups"], [[], []])
        self.assertEqual([g["n"] for g in comparison["groups"]], [3, 3])
        net = next(m for m in comparison["metrics"] if m["name"] == "finalNetWorthSen")
        for group, batch in zip(net["groups"], (s01, s03)):
            values = [v["value"] for v in group["values"]]
            self.assertEqual(len(set(values)), 1)  # mock agents are deterministic
            self.assertEqual((group["n"], group["sd"], group["ci95"]), (3, 0.0, None))
            self.assertEqual({v["runId"] for v in group["values"]}, set(batch["runIds"]))

    def test_unfinished_and_cancelled_runs_are_listed_as_excluded(self):
        _, batch = call("POST", "/batches", {"config": config(), "count": 2, "start": True})
        http("POST", f"/internal/runs/{batch['runIds'][0]}/cancel", {"reason": "test"})
        _, other = call("POST", "/batches", {"config": config(scenario="S03"), "count": 1, "start": False})
        comparison = call("GET", f"/compare?batch={batch['batchId']}&batch={other['batchId']}")[1]
        excluded = {e["runId"]: e["reason"] for g in comparison["groups"] for e in g["excluded"]}
        self.assertEqual(excluded[batch["runIds"][0]], "cancelled")
        self.assertEqual(excluded[batch["runIds"][1]], "not finished")
        self.assertEqual(excluded[other["runIds"][0]], "not finished")
        self.assertEqual([g["n"] for g in comparison["groups"]], [0, 0])
        for run_id in batch["runIds"][1:]:
            http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test cleanup"})

    def test_comparison_needs_two_to_six_known_batches(self):
        _, batch = call("POST", "/batches", {"config": config(), "count": 1})
        self.assertEqual(call("GET", f"/compare?batch={batch['batchId']}")[0], 400)
        import uuid
        self.assertEqual(call("GET", f"/compare?batch={uuid.uuid4()}&batch=" + batch["batchId"])[0], 404)


if __name__ == "__main__":
    unittest.main()
