"""ADR-0013 A4 (item 15): runs executed in parallel never share events or accounts, and match the same runs alone."""

import os
import threading
import unittest

import _path  # noqa: F401
from test_end_to_end import API, TOKEN, create_and_start, events, http, test_worker, trace

TERMINAL = {"COMPLETED", "FAILED", "CANCELLED", "BUDGET_EXCEEDED"}
CONFIGS = [dict(scenario=s, model=m, seed=seed) for seed, (s, m) in enumerate(
    [("S01", "finmind-mock-v1"), ("S02", "finmind-mock-adversarial-v1"), ("S03", "finmind-mock-chaotic-v1"),
     ("S02", "finmind-mock-v1"), ("S01", "finmind-mock-adversarial-v1"), ("S03", "finmind-mock-v1")], start=11)]


def drive(run_ids, workers):
    errors = []

    def loop():
        try:
            worker = test_worker()
            for _ in range(200):
                if all(http("GET", f"/internal/runs/{r}")["status"] in TERMINAL for r in run_ids):
                    return
                worker.run_once()
        except Exception as error:  # surfaced below
            errors.append(error)

    threads = [threading.Thread(target=loop) for _ in range(workers)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(300)
    if errors:
        raise errors[0]


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(os.environ.get("FINMIND_RESEARCHER_TOKEN", "")) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ParallelIsolationTests(unittest.TestCase):
    def test_parallel_runs_are_isolated_and_equal_to_sequential_runs(self):
        while test_worker().run_once() is not None:  # start from an empty queue
            pass
        parallel = [create_and_start(**config) for config in CONFIGS]
        drive(parallel, workers=len(CONFIGS))

        owned = {}
        for run_id in parallel:
            evs = events(run_id)
            self.assertTrue(evs and all(e["runId"] == run_id for e in evs))
            self.assertEqual([e["sequence"] for e in evs], list(range(1, len(evs) + 1)))
            owned[run_id] = {e["payload"]["accountId"] for e in evs if e["eventType"] == "ACCOUNT_CREATED"}
            used = {p["accountId"] for e in evs if e["eventType"] == "LEDGER_COMMITTED" for p in e["payload"]["postings"]}
            self.assertLessEqual(used, owned[run_id])
            self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])
        for index, run_id in enumerate(parallel):
            for other in parallel[index + 1:]:
                self.assertFalse(owned[run_id] & owned[other])

        sequential = []
        for config in CONFIGS:
            run_id = create_and_start(**config)
            drive([run_id], workers=1)
            sequential.append(run_id)
        for config, alone, together in zip(CONFIGS, sequential, parallel):
            with self.subTest(**config):
                self.assertEqual(http("GET", f"/internal/runs/{together}")["status"], http("GET", f"/internal/runs/{alone}")["status"])
                self.assertEqual(trace(together), trace(alone))


if __name__ == "__main__":
    unittest.main()
