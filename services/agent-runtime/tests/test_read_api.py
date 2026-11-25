"""ADR-0013 part B1: the researcher read API, against the real API and database."""

import json
import threading
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from finmind_runtime.client import Client
from test_end_to_end import API, RESEARCHER, TOKEN, create_and_start, events, http, run_until_terminal, test_worker

EVIDENCE = {"proposed", "denied", "committed", "reported_by_agent"}


def get(path):
    return http("GET", "/api/v1" + path)


def status_of(path):
    request = urllib.request.Request(API + "/api/v1" + path, headers={"Authorization": "Bearer " + RESEARCHER})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def pages(path, key, cursor="afterSequence", sort="proposedSequence"):
    items, after = [], 0
    while True:
        page = get(f"{path}?limit=7&{cursor}={after}")
        items += page[key]
        if not page[key]:
            return items
        after = page["next" + cursor[0].upper() + cursor[1:]]


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ReadApiTests(unittest.TestCase):
    def test_actions_carry_one_evidence_label_each(self):
        run_id = create_and_start(scenario="S02", model="finmind-mock-adversarial-v1")
        run_until_terminal(run_id)
        actions = pages(f"/runs/{run_id}/actions", "actions")
        evs = events(run_id)
        proposed = [e["traceId"] for e in evs if e["eventType"] == "TOOL_CALL_PROPOSED"]
        self.assertEqual([a["toolCallId"] for a in actions], proposed)  # each once, in event order
        committed_by = {e["traceId"]: e["journalId"] for e in evs if e["eventType"] == "LEDGER_COMMITTED" and e["traceId"]}
        for action in actions:
            with self.subTest(action["toolCallId"]):
                self.assertIn(action["evidence"], EVIDENCE)
                self.assertEqual(action["evidence"] == "committed", action["toolCallId"] in committed_by)
                if action["evidence"] == "committed":
                    self.assertEqual(action["journalId"], committed_by[action["toolCallId"]])
        labels = {a["evidence"] for a in actions}
        self.assertTrue({"committed", "denied", "reported_by_agent", "proposed"} <= labels)
        counts = get(f"/runs/{run_id}")["actionCounts"]
        self.assertEqual(counts["total"], len(actions))
        self.assertEqual(sum(counts[label] for label in EVIDENCE), counts["total"])

    def test_wallets_and_ledger_are_postings_and_reconcile(self):
        run_id = create_and_start(scenario="S01")
        run_until_terminal(run_id)
        wallets = get(f"/runs/{run_id}/wallets")
        evs = events(run_id)
        self.assertEqual(wallets["asOfSequence"], evs[-1]["sequence"])
        self.assertTrue(wallets["reconciled"])
        self.assertEqual({w["kind"] for w in wallets["accounts"]}, {"cash", "savings"})
        for wallet in wallets["accounts"]:
            self.assertEqual((wallet["evidence"], wallet["balanceSen"]), ("verified_by_ledger", wallet["eventBalanceSen"]))
            self.assertEqual(wallet["balanceSen"],
                             http("GET", f"/internal/runs/{run_id}/accounts/{wallet['accountId']}/balance")["balanceSen"])
        journals = pages(f"/runs/{run_id}/ledger", "journals")
        self.assertEqual(len(journals), sum(e["eventType"] == "LEDGER_COMMITTED" for e in evs))
        for journal in journals:
            self.assertEqual(sum(p["amountSen"] for p in journal["postings"]), 0)
            self.assertEqual((journal["evidence"], journal["effective"]), ("verified_by_ledger", True))

    def test_reversal_shows_on_both_journals(self):
        run_id = create_and_start(scenario="S01")
        client = Client(API, TOKEN)
        while True:
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                break
            client.finish("failed", "test: cleared from the shared test queue")
        client.initialize()
        saved = client.tool_call("turn-0001", "move_to_savings", {"amount_sen": 500, "idempotency_key": "s"})
        reversal = http("POST", f"/internal/runs/{run_id}/journals/{saved['journal_id']}/reversals",
                        {"idempotencyKey": "undo", "reason": "test"})
        journals = {j["journalId"]: j for j in pages(f"/runs/{run_id}/ledger", "journals")}
        original = journals[saved["journal_id"]]
        self.assertEqual((original["effective"], original["reversedByJournalId"]), (False, reversal["journalId"]))
        self.assertEqual(journals[reversal["journalId"]]["reversesJournalId"], saved["journal_id"])
        action = get(f"/runs/{run_id}/actions")["actions"][0]
        self.assertEqual((action["evidence"], action["journalId"]), ("committed", saved["journal_id"]))
        client.finish("completed", "test")

    def test_run_detail_and_list(self):
        run_id = create_and_start(scenario="S03", seed=5)
        run_until_terminal(run_id)
        detail = get(f"/runs/{run_id}")
        self.assertEqual((detail["runId"], detail["managed"], detail["researchEligible"]), (run_id, True, True))
        self.assertEqual(detail["configSnapshot"]["seed"], 5)
        budgets = detail["budgets"]
        self.assertEqual(budgets["actions"]["used"], detail["actionCounts"]["total"])
        self.assertEqual(budgets["actions"]["max"], detail["configSnapshot"]["maxActions"])
        self.assertEqual(set(budgets), {"actions", "modelTokens", "costMicroUsd", "wallTimeSeconds"})

        legacy = str(uuid.uuid4())
        http("POST", f"/internal/runs/{legacy}/funding", {"idempotencyKey": "o", "amountSen": 1000, "transactionLimitSen": 500})
        old = get(f"/runs/{legacy}")
        self.assertEqual((old["managed"], old["researchEligible"], old["budgets"]), (False, False, None))

        listed = get("/runs?limit=500")["runs"]
        self.assertEqual(listed[0]["runId"], legacy)  # newest first
        self.assertTrue(all(r["status"] == "COMPLETED" for r in get("/runs?status=COMPLETED&limit=50")["runs"]))
        self.assertTrue(all(not r["managed"] for r in get("/runs?kind=legacy&limit=50")["runs"]))
        first = get("/runs?limit=3")
        second = get(f"/runs?limit=3&before={first['nextBefore']}")
        self.assertFalse({r["runId"] for r in first["runs"]} & {r["runId"] for r in second["runs"]})

    def test_errors_and_catalogs(self):
        self.assertEqual(status_of(f"/runs/{uuid.uuid4()}"), 404)
        self.assertEqual(status_of("/runs?status=NOPE"), 400)
        self.assertEqual(status_of("/runs?kind=other"), 400)
        self.assertIn("S01", {s["id"] for s in get("/scenarios")["scenarios"]})
        models = get("/models")["models"]
        self.assertIn(("mock", "finmind-mock-v1"), {(m["provider"], m["modelId"]) for m in models})
        self.assertIn(("anthropic", "claude-opus-5-5"), {(m["provider"], m["modelId"]) for m in models})

    def test_reads_during_writes_have_no_gap_duplicate_or_mismatch(self):
        while test_worker().run_once() is not None:
            pass
        run_id = create_and_start(scenario="S02", model="finmind-mock-chaotic-v1", seed=9)
        worker = threading.Thread(target=lambda: run_until_terminal(run_id))
        worker.start()
        seen_events, seen_actions, after_event, after_action = [], [], 0, 0
        while True:
            alive = worker.is_alive()
            wallets = get(f"/runs/{run_id}/wallets")
            self.assertTrue(wallets["reconciled"], wallets)
            page = get(f"/runs/{run_id}/events?limit=5&afterSequence={after_event}")
            seen_events += [e["sequence"] for e in page["events"]]
            after_event = page["nextAfterSequence"]
            page = get(f"/runs/{run_id}/actions?limit=5&afterSequence={after_action}")
            seen_actions += [a["toolCallId"] for a in page["actions"]]
            after_action = page["nextAfterSequence"]
            if not alive and not page["actions"] and seen_events and seen_events[-1] == events(run_id)[-1]["sequence"]:
                break
        worker.join()
        self.assertEqual(seen_events, [e["sequence"] for e in events(run_id)])
        self.assertEqual(seen_actions, [e["traceId"] for e in events(run_id) if e["eventType"] == "TOOL_CALL_PROPOSED"])

    def test_no_credential_is_ever_returned(self):
        run_id = create_and_start(scenario="S01")
        client = Client(API, TOKEN)
        while True:
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                break
            client.finish("failed", "test: cleared from the shared test queue")
        client.initialize()
        # The agent never holds the worker, researcher, or session token; a provider-style key is redacted on entry.
        client.tool_call("turn-0001", "submit_plan", {"text": "key sk-ant-abcdefghijklmnop"})
        bodies = [json.dumps(get(path)) for path in
                  (f"/runs/{run_id}", f"/runs/{run_id}/actions", f"/runs/{run_id}/events", f"/runs/{run_id}/ledger",
                   f"/runs/{run_id}/wallets", "/runs?limit=500", "/models", "/scenarios")]
        for body in bodies:
            for secret in (TOKEN, RESEARCHER, claim["sessionToken"], "sk-ant-abcdefghijklmnop"):
                self.assertNotIn(secret, body)
        client.finish("completed", "test")


if __name__ == "__main__":
    unittest.main()


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ModelCatalogApiTests(unittest.TestCase):
    """ADR-0019 step 1: each model names its provider, its price version, and whether it was verified live."""

    def test_models_state_their_provider_prices_and_live_status(self):
        models = get("/models")["models"]
        mocks = [m for m in models if m["provider"] == "mock"]
        paid = [m for m in models if m["paid"]]
        self.assertTrue(mocks and paid)
        for m in mocks:
            self.assertEqual((m["pricingVersion"], m["verifiedLive"]), ("none", True))
        for m in paid:
            self.assertTrue(m["pricingVersion"].startswith(m["provider"] + "-pricing/"), m)
            self.assertIs(m["verifiedLive"], False, m)
            self.assertTrue(m["providerLabel"])
