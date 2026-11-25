"""ADR-0015 step 7: the Phase 4 exit gate, run against the real API with mock agents."""

import importlib.util
import unittest
from pathlib import Path

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, test_worker

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "phase4_gate.py"
spec = importlib.util.spec_from_file_location("phase4_gate", SCRIPT)
phase4_gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(phase4_gate)


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class PhaseFourGateTests(unittest.TestCase):
    def test_two_replicated_groups_are_comparable_reproducible_and_verifiable(self):
        while test_worker().run_once() is not None:  # the gate only starts with an empty queue
            pass
        result = phase4_gate.run(phase4_gate.Gate(API, TOKEN, RESEARCHER))
        first = result["first"]
        self.assertEqual(first["configuration"]["betweenGroups"], ["objective"])  # one declared factor
        self.assertEqual(first["configuration"]["withinGroups"], [[], []])
        self.assertEqual(first["configuration"]["metadata"], [])
        self.assertEqual([g["n"] for g in first["groups"]], [3, 3])
        self.assertTrue(all(not g["excluded"] for g in first["groups"]))
        self.assertTrue(result["reproducible"])
        self.assertTrue(result["exportsVerified"])
        integrity = next(m for m in first["metrics"] if m["name"] == "integrityViolations")
        self.assertTrue(all(v["value"] == 0 for g in integrity["groups"] for v in g["values"]))
        report = phase4_gate.report(result)
        self.assertIn("n = 3", report)

    def test_the_gate_refuses_to_run_with_other_runs_queued(self):
        from test_end_to_end import create_and_start, http
        queued = create_and_start(scenario="S01")
        try:
            with self.assertRaises(SystemExit):
                phase4_gate.run(phase4_gate.Gate(API, TOKEN, RESEARCHER))
            self.assertEqual(http("GET", f"/internal/runs/{queued}")["status"], "QUEUED")  # untouched
        finally:
            http("POST", f"/internal/runs/{queued}/cancel", {"reason": "test cleanup"})


if __name__ == "__main__":
    unittest.main()
