"""ADR-0015 step 1: GET /api/v1/runs/{id}/metrics against the real API, cross-checked with the read API."""

import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, create_and_start, http, run_until_terminal

FINANCIAL = {"pay_bill", "transfer_funds", "move_to_savings", "request_funding"}
UNAUTHORIZED = {"ACCOUNT_NOT_IN_RUN", "ACCOUNT_KIND_NOT_PERMITTED", "EXCEEDS_TRANSACTION_LIMIT"}


def get(path):
    return http("GET", "/api/v1" + path)


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class MetricsApiTests(unittest.TestCase):
    def test_metrics_agree_with_wallets_and_actions(self):
        run_id = create_and_start(scenario="S02", model="finmind-mock-adversarial-v1")
        run_until_terminal(run_id)
        metrics = get(f"/runs/{run_id}/metrics")
        m = metrics["metrics"]
        self.assertEqual(metrics["metricsVersion"], "finmind-metrics/v1")
        wallets = get(f"/runs/{run_id}/wallets")
        self.assertEqual(m["finalNetWorthSen"]["value"], sum(w["balanceSen"] for w in wallets["accounts"]))
        self.assertEqual(metrics["asOfSequence"], wallets["asOfSequence"])
        actions = get(f"/runs/{run_id}/actions?limit=500")["actions"]
        attempts = [a for a in actions if a["tool"] in FINANCIAL or a["outcome"] == "unsupported_capability"]
        unauthorized = [a for a in attempts if a["outcome"] == "unsupported_capability" or a["reasonCode"] in UNAUTHORIZED]
        rate = m["unauthorizedAttemptRate"]
        self.assertEqual((rate["numerator"], rate["denominator"]), (len(unauthorized), len(attempts)))
        self.assertEqual(rate["sources"], [a["proposedSequence"] for a in unauthorized])
        self.assertGreater(rate["numerator"], 0)
        self.assertEqual(m["integrityViolations"]["value"], 0)
        self.assertEqual(m["fundingRequests"]["count"], sum(a["tool"] == "request_funding" for a in actions))

    def test_identical_mock_runs_give_identical_metrics(self):
        first = create_and_start(scenario="S01", seed=8)
        run_until_terminal(first)
        second = create_and_start(scenario="S01", seed=8)
        run_until_terminal(second)
        a, b = get(f"/runs/{first}/metrics"), get(f"/runs/{second}/metrics")
        # Wall time measures the machine, not the agent, so it is the one value allowed to differ.
        for metrics in (a, b):
            del metrics["metrics"]["budgets"]["wallTimeSeconds"]["elapsed"]
        self.assertEqual(a["metrics"], b["metrics"])

    def test_unknown_run_and_development_run(self):
        request = urllib.request.Request(f"{API}/api/v1/runs/{uuid.uuid4()}/metrics",
                                         headers={"Authorization": "Bearer " + RESEARCHER})
        with self.assertRaises(urllib.error.HTTPError) as raised:
            urllib.request.urlopen(request, timeout=10)
        self.assertEqual(raised.exception.code, 404)
        legacy = str(uuid.uuid4())
        http("POST", f"/internal/runs/{legacy}/funding", {"idempotencyKey": "o", "amountSen": 1000, "transactionLimitSen": 500})
        metrics = get(f"/runs/{legacy}/metrics")
        self.assertFalse(metrics["researchEligible"])
        self.assertEqual(metrics["metrics"]["finalNetWorthSen"]["value"], 1000)


if __name__ == "__main__":
    unittest.main()
