"""ADR-0013 A2 regression tests: direct routes on managed runs, and truncation on code point boundaries."""

import json
import os
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from finmind_runtime.client import Client

API = os.environ.get("FINMIND_E2E_API_URL")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")


def units(text):
    return len(text.encode("utf-16-le")) // 2


class TruncationTests(unittest.TestCase):
    def sent(self, action):
        bodies = []
        client = Client("http://api", "w" * 32, lambda *args: (bodies.append(json.loads(args[3])), (200, b"{}"))[1])
        client.session_token = "s"
        action(client)
        return bodies[0]

    def test_tool_name_is_cut_by_the_units_the_server_counts(self):
        name = self.sent(lambda c: c.tool_call("turn-0001", "a" + "\U0001F600" * 100, {}))["tool"]
        self.assertEqual(name, "a" + "\U0001F600" * 49)
        self.assertLessEqual(units(name), 100)

    def test_output_text_is_cut_between_code_points(self):
        text = self.sent(lambda c: c.model_invocation(1, 1, 1, 1, "a" + "\U0001F600" * 60_000))["outputText"]
        self.assertEqual(text, "a" + "\U0001F600" * 44_999)


def post(path, body):
    from test_end_to_end import RESEARCHER
    request = urllib.request.Request(API + path, json.dumps(body).encode(),
                                     {"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        with error:
            return error.code, json.loads(error.read())


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN")
class DirectRouteTests(unittest.TestCase):
    def test_direct_routes_cannot_change_a_managed_run(self):
        from test_end_to_end import config, events, http
        run_id = http("POST", "/internal/runs", config(scenario="S02"))["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        client = Client(API, TOKEN)
        for _ in range(1000):
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                break
            client.finish("failed", "test: cleared from the shared test queue")
        else:
            self.fail("run was not claimed")
        client.initialize()
        client.tool_call("turn-0001", "wait", {"days": 1})
        listed = client.tool_call("turn-0002", "list_obligations", {})["data"]
        bill, payee = listed["bills"][0]["bill_id"], listed["payees"][0]["destination_id"]
        accounts = {e["payload"]["kind"]: e["payload"]["accountId"]
                    for e in events(run_id) if e["eventType"] == "ACCOUNT_CREATED"}
        cash, savings = accounts["agent_cash"], accounts["agent_savings"]
        before = client.tool_call("turn-0003", "get_balance", {})["data"]
        seen = len(events(run_id))

        # Every body is valid, so only the managed-run rule can reject it.
        requests = {
            "accounts": {"kind": "Merchant"},
            "transfers": {"idempotencyKey": "d1", "sourceAccountId": cash, "destinationAccountId": payee, "amountSen": 100},
            "savings": {"idempotencyKey": "d2", "cashAccountId": cash, "savingsAccountId": savings, "amountSen": 100},
            "bill-payments": {"idempotencyKey": "d3", "cashAccountId": cash, "obligationId": bill},
            "obligations": {"merchantAccountId": payee, "amountSen": 100, "invoiceReference": "INJECTED"},
        }
        for route, body in requests.items():
            with self.subTest(route=route):
                status, response = post(f"/internal/runs/{run_id}/{route}", body)
                self.assertEqual((status, response.get("reasonCode")), (422, "DIRECT_WRITE_NOT_ALLOWED"))
        status, _ = post(f"/internal/runs/{run_id}/funding",
                         {"idempotencyKey": "d4", "amountSen": 100, "transactionLimitSen": 100})
        self.assertGreaterEqual(status, 400)

        self.assertEqual(client.tool_call("turn-0004", "get_balance", {})["data"], before)
        recorded = [e for e in events(run_id)[seen:] if e["reasonCode"] == "DIRECT_WRITE_NOT_ALLOWED"]
        self.assertEqual([(e["eventType"], e["source"]) for e in recorded], [("POLICY_DENIED", "internal_api")] * 5)
        self.assertEqual(client.tool_call("turn-0005", "pay_bill", {"bill_id": bill, "idempotency_key": "agent-pays"})["status"],
                         "committed")  # the agent's own path still works
        client.finish("completed", "test")

    def test_unmanaged_runs_keep_the_direct_routes(self):
        run = f"/internal/runs/{uuid.uuid4()}"
        status, funded = post(f"{run}/funding", {"idempotencyKey": "open", "amountSen": 1000, "transactionLimitSen": 500})
        self.assertEqual(status, 200)
        status, account = post(f"{run}/accounts", {"kind": "Merchant"})
        self.assertEqual(status, 201)
        status, _ = post(f"{run}/transfers", {"idempotencyKey": "t", "sourceAccountId": funded["cashAccountId"],
                                              "destinationAccountId": account["accountId"], "amountSen": 100})
        self.assertEqual(status, 200)


if __name__ == "__main__":
    unittest.main()
