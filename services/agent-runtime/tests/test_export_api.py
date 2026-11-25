"""ADR-0015 step 4: exports, verified independently with the standard library only."""

import csv
import hashlib
import io
import json
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, create_and_start, events, http, run_until_terminal


def raw(path):
    request = urllib.request.Request(API + "/api/v1" + path, headers={"Authorization": "Bearer " + RESEARCHER})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        with error:
            return error.code, dict(error.headers), error.read()


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.run_id = create_and_start(scenario="S02", model="finmind-mock-adversarial-v1")
        run_until_terminal(cls.run_id)
        status, headers, body = raw(f"/runs/{cls.run_id}/export?format=jsonl")
        assert status == 200, status
        cls.headers = headers
        cls.records = [json.loads(line) for line in body.decode("utf-8").splitlines()]

    def of(self, kind):
        return [r for r in self.records if r["type"] == kind]

    def test_header_states_versions_and_redaction(self):
        header = self.records[0]
        self.assertEqual(header["type"], "header")
        self.assertEqual(header["exportSchemaVersion"], "finmind-export/v1")
        self.assertEqual((header["metricsVersion"], header["classificationVersion"]),
                         ("finmind-metrics/v1", "finmind-classifications/v1"))
        self.assertIn("redact", header["redaction"].lower())
        self.assertEqual(self.records[-1]["type"], "end")
        self.assertEqual(self.records[-1]["counts"]["events"], len(self.of("event")))
        self.assertIn("attachment", self.headers.get("Content-Disposition", ""))
        self.assertEqual(self.headers["Content-Type"].split(";")[0], "application/x-ndjson")

    def test_chain_verifies_with_hashlib_alone(self):
        previous = None
        exported = self.of("event")
        self.assertEqual([e["sequence"] for e in exported], [e["sequence"] for e in events(self.run_id)])
        for evt in exported:
            self.assertEqual(hashlib.sha256(evt["hashInput"].encode("utf-8")).hexdigest(), evt["eventHash"])
            self.assertEqual(evt["prevHash"], previous)
            previous = evt["eventHash"]
            if evt["hashVersion"] == 2:
                hashed = json.loads(evt["hashInput"].split("\n", 1)[1])
                # What was hashed is what was exported.
                for field, key in (("eventType", "event_type"), ("sequence", "sequence"), ("source", "source"),
                                   ("traceId", "trace_id"), ("outcome", "outcome"), ("reasonCode", "reason_code"),
                                   ("payload", "payload"), ("prevHash", "prev_hash")):
                    self.assertEqual(evt[field], hashed[key], field)

    def test_ledger_totals_rebuild_the_wallets(self):
        kinds = {}
        balances = {}
        for journal in self.of("journal"):
            self.assertEqual(sum(p["amountSen"] for p in journal["postings"]), 0)
            for posting in journal["postings"]:
                kinds[posting["accountId"]] = posting["accountKind"]
                balances[posting["accountId"]] = balances.get(posting["accountId"], 0) + posting["amountSen"]
        wallets = http("GET", f"/api/v1/runs/{self.run_id}/wallets")
        for wallet in wallets["accounts"]:
            self.assertEqual(balances.get(wallet["accountId"], 0), wallet["balanceSen"])
        exported_wallets = self.of("wallets")[0]
        self.assertEqual(exported_wallets["accounts"], wallets["accounts"])

    def test_derived_results_match_the_read_api(self):
        metrics = http("GET", f"/api/v1/runs/{self.run_id}/metrics")
        self.assertEqual(self.of("metrics")[0]["metrics"], metrics["metrics"])
        classified = http("GET", f"/api/v1/runs/{self.run_id}/classifications")
        self.assertEqual(self.of("classifications")[0]["items"], classified["items"])
        self.assertEqual(len(self.of("action")), len(http("GET", f"/api/v1/runs/{self.run_id}/actions?limit=500")["actions"]))
        self.assertEqual(self.of("run")[0]["configSnapshot"]["scenarioId"], "S02")

    def test_csv_lists_every_posting(self):
        status, headers, body = raw(f"/runs/{self.run_id}/export?format=csv")
        self.assertEqual((status, headers["Content-Type"].split(";")[0]), (200, "text/csv"))
        rows = list(csv.DictReader(io.StringIO(body.decode("utf-8"))))
        postings = [p for j in self.of("journal") for p in j["postings"]]
        self.assertEqual(len(rows), len(postings))
        self.assertEqual(sum(int(r["amount_sen"]) for r in rows), 0)
        self.assertEqual({r["account_kind"] for r in rows} >= {"agent_cash", "simulator_treasury"}, True)

    def test_no_credential_appears_and_errors_are_clean(self):
        for fmt in ("jsonl", "csv"):
            _, _, body = raw(f"/runs/{self.run_id}/export?format={fmt}")
            for secret in (TOKEN, RESEARCHER):
                self.assertNotIn(secret.encode(), body)
        self.assertEqual(raw(f"/runs/{uuid.uuid4()}/export?format=jsonl")[0], 404)
        self.assertEqual(raw(f"/runs/{self.run_id}/export?format=xml")[0], 400)
        self.assertEqual(raw(f"/runs/{self.run_id}/export")[0], 400)


if __name__ == "__main__":
    unittest.main()
