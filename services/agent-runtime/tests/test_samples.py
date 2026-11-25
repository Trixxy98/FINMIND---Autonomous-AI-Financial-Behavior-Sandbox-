"""ADR-0017 step 6: the sample exports are valid, synthetic, current, and reproducible."""

import copy
import csv
import hashlib
import json
import unittest
from pathlib import Path

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, http, test_worker

SAMPLES = Path(__file__).resolve().parents[3] / "samples"
CONFIGS = json.loads((SAMPLES / "configs.json").read_text(encoding="utf-8"))["runs"]
MACHINE_DEPENDENT = [("budgets", "wallTimeSeconds", "elapsed"), ("budgets", "modelLatency", "totalMs")]


def records(name):
    return [json.loads(line) for line in (SAMPLES / f"{name}.jsonl").read_text(encoding="utf-8").splitlines()]


def of(rows, kind):
    return [r for r in rows if r["type"] == kind]


def comparable(metrics):
    """Metrics without the fields that measure the machine."""
    metrics = copy.deepcopy(metrics)
    for path in MACHINE_DEPENDENT:
        node = metrics
        for key in path[:-1]:
            node = node.get(key, {})
        node.pop(path[-1], None)
    return metrics


class SampleFileTests(unittest.TestCase):
    def test_every_configuration_has_both_files_and_nothing_else_is_there(self):
        expected = {"configs.json", "README.md"} | {f"{c['name']}.{ext}" for c in CONFIGS for ext in ("jsonl", "csv")}
        self.assertEqual({p.name for p in SAMPLES.iterdir()}, expected)
        self.assertLess(sum(p.stat().st_size for p in SAMPLES.iterdir()), 1_000_000)

    def test_each_chain_verifies_with_hashlib_alone(self):
        for sample in CONFIGS:
            rows = records(sample["name"])
            previous = None
            for event in of(rows, "event"):
                self.assertEqual(hashlib.sha256(event["hashInput"].encode()).hexdigest(), event["eventHash"], sample["name"])
                self.assertEqual(event["prevHash"], previous, sample["name"])
                previous = event["eventHash"]
            self.assertTrue(of(rows, "end")[0]["chainIntact"])

    def test_journals_balance_and_rebuild_the_wallets(self):
        for sample in CONFIGS:
            rows = records(sample["name"])
            totals = {}
            for journal in of(rows, "journal"):
                self.assertEqual(sum(p["amountSen"] for p in journal["postings"]), 0, sample["name"])
                for p in journal["postings"]:
                    totals[p["accountId"]] = totals.get(p["accountId"], 0) + p["amountSen"]
            for wallet in of(rows, "wallets")[0]["accounts"]:
                self.assertEqual(totals.get(wallet["accountId"], 0), wallet["balanceSen"], sample["name"])
            with open(SAMPLES / f"{sample['name']}.csv", newline="", encoding="utf-8") as f:
                self.assertEqual(sum(int(r["amount_sen"]) for r in csv.DictReader(f)), 0)

    def test_each_file_is_the_configuration_it_claims_and_uses_current_versions(self):
        for sample in CONFIGS:
            rows = records(sample["name"])
            snapshot = of(rows, "run")[0]["configSnapshot"]
            for key, value in sample["config"].items():
                self.assertEqual(snapshot[key] if key != "agent" else {k: snapshot["agent"][k] for k in value}, value,
                                 f"{sample['name']}: {key}")
            header = of(rows, "header")[0]
            self.assertEqual((header["exportSchemaVersion"], header["metricsVersion"], header["reasonCodeCatalog"]),
                             ("finmind-export/v1", "finmind-metrics/v1", "finance-reasons/v8"), sample["name"])
            self.assertEqual(snapshot["agent"]["provider"], "mock")

    def test_no_secret_or_real_identifier_is_in_a_sample(self):
        for path in SAMPLES.glob("*.*"):
            text = path.read_text(encoding="utf-8")
            self.assertNotRegex(text, r"Password\s*=|sk-ant-|Bearer\s+[A-Za-z0-9]", path.name)
            self.assertNotRegex(text, r"(?<![0-9a-f])[0-9a-f]{48}(?![0-9a-f])", path.name)  # the length of generated tokens


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class SampleReproductionTests(unittest.TestCase):
    def test_the_same_configurations_give_the_same_metrics(self):
        while test_worker().run_once() is not None:
            pass
        for sample in CONFIGS:
            run = http("POST", "/api/v1/runs", sample["config"])["runId"]
            http("POST", f"/api/v1/runs/{run}/start")
            while (result := test_worker().run_once()) is not None and result.run_id != run:
                pass
            fresh = http("GET", f"/api/v1/runs/{run}/metrics")["metrics"]
            stored = of(records(sample["name"]), "metrics")[0]["metrics"]
            self.assertEqual(comparable(fresh), comparable(stored), sample["name"])


if __name__ == "__main__":
    unittest.main()
