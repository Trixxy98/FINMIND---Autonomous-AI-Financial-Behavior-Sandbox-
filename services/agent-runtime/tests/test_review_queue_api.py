"""ADR-0015 step 5b: the cross-run review queue, against the real API and database."""

import json
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, create_and_start, http, run_until_terminal


def call(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + "/api/v1" + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        with error:
            return error.code, json.loads(error.read() or b"null")


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ReviewQueueTests(unittest.TestCase):
    def test_finished_research_runs_with_open_candidates_are_listed_until_decided(self):
        run_id = create_and_start(scenario="S02", model="finmind-mock-adversarial-v1")
        run_until_terminal(run_id)
        status, queue = call("GET", "/reviews/pending?limit=50")
        self.assertEqual(status, 200)
        self.assertEqual(queue["classificationVersion"], "finmind-classifications/v1")
        entry = next(r for r in queue["runs"] if r["runId"] == run_id)
        items = http("GET", f"/api/v1/runs/{run_id}/classifications")["items"]
        open_items = [i for i in items if i["status"] == "needs_review"]
        self.assertEqual((entry["unresolved"], entry["scenarioId"]), (len(open_items), "S02"))
        self.assertGreater(entry["unresolved"], 0)
        for item in open_items:
            call("POST", f"/runs/{run_id}/reviews", {"key": item["key"], "label": "NORMAL_ACTION", "note": "test"})
        after = call("GET", "/reviews/pending?limit=50")[1]
        self.assertNotIn(run_id, [r["runId"] for r in after["runs"]])

    def test_unfinished_and_development_runs_are_not_queued(self):
        queued = create_and_start(scenario="S01")  # not run
        legacy = str(uuid.uuid4())
        http("POST", f"/internal/runs/{legacy}/funding", {"idempotencyKey": "o", "amountSen": 1000, "transactionLimitSen": 500})
        ids = [r["runId"] for r in call("GET", "/reviews/pending?limit=50")[1]["runs"]]
        self.assertNotIn(queued, ids)
        self.assertNotIn(legacy, ids)
        http("POST", f"/internal/runs/{queued}/cancel", {"reason": "test cleanup"})

    def test_limit_is_bounded(self):
        self.assertEqual(call("GET", "/reviews/pending?limit=0")[0], 200)
        self.assertLessEqual(call("GET", "/reviews/pending?limit=9999")[1]["scanned"], 100)


if __name__ == "__main__":
    unittest.main()
