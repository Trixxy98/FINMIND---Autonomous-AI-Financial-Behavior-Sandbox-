"""ADR-0014 step 1: run control under /api/v1, and the agent's observation on each action."""

import json
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from finmind_runtime.client import Client
from test_end_to_end import API, RESEARCHER, TOKEN, config, events, http, run_until_terminal


def call(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + "/api/v1" + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, json.loads(response.read() or b"null"), response.headers.get("Location")
    except urllib.error.HTTPError as error:
        with error:
            return error.code, json.loads(error.read() or b"null"), None


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class RunControlTests(unittest.TestCase):
    def test_create_start_run_and_verify(self):
        status, run, location = call("POST", "/runs", config(scenario="S01"))
        self.assertEqual((status, run["status"], location), (201, "DRAFT", f"/api/v1/runs/{run['runId']}"))
        internal = http("GET", f"/internal/runs/{run['runId']}")
        self.assertEqual(run["configHash"], internal["configHash"])
        status, started, _ = call("POST", f"/runs/{run['runId']}/start")
        self.assertEqual((status, started["status"]), (200, "QUEUED"))
        run_until_terminal(run["runId"])
        status, verified, _ = call("GET", f"/runs/{run['runId']}/verify")
        self.assertEqual((status, verified), (200, {"runId": run["runId"], "intact": True, "brokenAtSequence": None}))

    def test_invalid_configuration_is_rejected_with_field_names(self):
        bad = config()
        bad["seed"] = -1
        status, body, _ = call("POST", "/runs", bad)
        self.assertEqual((status, body["reasonCode"], body["fields"]), (400, "INVALID_RUN_CONFIG", ["seed"]))

    def test_cancel_needs_a_reason_and_is_recorded(self):
        _, run, _ = call("POST", "/runs", config())
        run_id = run["runId"]
        call("POST", f"/runs/{run_id}/start")
        for body in (None, {}, {"reason": "   "}):
            with self.subTest(body=body):
                status, rejected, _ = call("POST", f"/runs/{run_id}/cancel", body)
                self.assertEqual(status, 400)
        status, cancelled, _ = call("POST", f"/runs/{run_id}/cancel", {"reason": "researcher stopped it"})
        self.assertEqual((status, cancelled["status"], cancelled["statusReason"]), (200, "CANCELLED", "researcher stopped it"))
        change = [e for e in events(run_id) if e["eventType"] == "RUN_STATUS_CHANGED"][-1]
        self.assertEqual((change["source"], change["payload"]["to"]), ("internal_api", "CANCELLED"))
        status, again, _ = call("POST", f"/runs/{run_id}/cancel", {"reason": "twice"})
        self.assertEqual((status, again["reasonCode"]), (409, "INVALID_RUN_TRANSITION"))

    def test_unknown_runs_are_404(self):
        missing = uuid.uuid4()
        for method, path, body in (("POST", f"/runs/{missing}/start", None), ("POST", f"/runs/{missing}/cancel", {"reason": "x"}),
                                   ("GET", f"/runs/{missing}/verify", None)):
            with self.subTest(path=path):
                self.assertEqual(call(method, path, body)[0], 404)

    def test_each_action_carries_the_response_the_agent_received(self):
        _, run, _ = call("POST", "/runs", config(scenario="S01"))
        run_id = run["runId"]
        call("POST", f"/runs/{run_id}/start")
        client = Client(API, TOKEN)
        while True:
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                break
            client.finish("failed", "test: cleared from the shared test queue")
        client.initialize()
        received = client.tool_call("turn-0001", "get_balance", {})
        denied = client.tool_call("turn-0002", "move_to_savings", {"amount_sen": 10 ** 9, "idempotency_key": "k"})
        actions = call("GET", f"/runs/{run_id}/actions")[1]["actions"]
        self.assertEqual([a["response"] for a in actions], [received, denied])
        client.finish("completed", "test")


if __name__ == "__main__":
    unittest.main()
