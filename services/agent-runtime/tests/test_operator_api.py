"""ADR-0017 step 2 over HTTP: the operator stop, and the export record."""

import json
import unittest
import urllib.error
import urllib.request
import uuid

import _path  # noqa: F401
from test_end_to_end import API, RESEARCHER, TOKEN, config, http


def send(method, path, payload=None, token=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + path, data=data, method=method, headers={
        "Content-Type": "application/json", "Authorization": "Bearer " + (RESEARCHER if token is None else token)})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, parse(response)
    except urllib.error.HTTPError as error:
        return error.code, parse(error)


def parse(response):
    raw = response.read()
    return json.loads(raw) if raw and "application/json" in (response.headers.get("Content-Type") or "") else raw


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class OperatorStopApiTests(unittest.TestCase):
    def tearDown(self):
        send("POST", "/api/v1/admin/resume", {"reason": "test cleanup"})

    def test_stop_cancels_runs_and_refuses_starts_and_claims_until_resumed(self):
        queued = http("POST", "/api/v1/runs", config())["runId"]
        http("POST", f"/api/v1/runs/{queued}/start")
        draft = http("POST", "/api/v1/runs", config())["runId"]

        status, body = send("POST", "/api/v1/admin/stop", {"reason": "drill"})
        self.assertEqual(status, 200)
        self.assertIn(queued, body["cancelledRunIds"])
        self.assertEqual(http("GET", f"/api/v1/runs/{queued}")["status"], "CANCELLED")
        self.assertEqual(http("GET", f"/api/v1/runs/{queued}")["statusReason"], "Operator stop: drill")

        status, body = send("POST", f"/api/v1/runs/{draft}/start")
        self.assertEqual((status, body["reasonCode"]), (409, "OPERATOR_STOPPED"))
        self.assertEqual(send("POST", "/internal/worker/runs/claim", {}, token=TOKEN)[0], 204)
        state = http("GET", "/api/v1/admin/status")
        self.assertEqual((state["stopped"], state["reason"], state["history"][0]["action"]), (True, "drill", "stop"))

        status, state = send("POST", "/api/v1/admin/resume", {"reason": "drill over"})
        self.assertEqual((status, state["stopped"]), (200, False))
        self.assertEqual(http("POST", f"/api/v1/runs/{draft}/start")["status"], "QUEUED")
        http("POST", f"/api/v1/runs/{draft}/cancel", {"reason": "test cleanup"})

    def test_stop_and_resume_need_a_reason_and_the_researcher_token(self):
        self.assertEqual(send("POST", "/api/v1/admin/stop", {"reason": "  "})[1]["reasonCode"], "MISSING_REQUIRED_FIELD")
        self.assertEqual(send("POST", "/api/v1/admin/resume", {})[1]["reasonCode"], "MISSING_REQUIRED_FIELD")
        self.assertEqual(send("POST", "/api/v1/admin/stop", {"reason": "x"}, token=TOKEN)[0], 401)
        self.assertEqual(send("GET", "/api/v1/admin/status", token="")[0], 401)
        self.assertFalse(http("GET", "/api/v1/admin/status")["stopped"])


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ExportLogApiTests(unittest.TestCase):
    def test_every_export_request_is_recorded_with_its_outcome(self):
        run = http("POST", "/api/v1/runs", config())["runId"]
        missing = str(uuid.uuid4())
        self.assertEqual(send("GET", f"/api/v1/runs/{run}/export?format=jsonl")[0], 200)
        self.assertEqual(send("GET", f"/api/v1/runs/{run}/export?format=xlsx")[0], 400)
        self.assertEqual(send("GET", f"/api/v1/runs/{missing}/export?format=csv")[0], 404)
        latest = http("GET", "/api/v1/admin/exports?limit=3")["exports"]
        self.assertEqual([(e["runId"], e["format"], e["outcome"]) for e in latest],
                         [(missing, "csv", "not_found"), (run, "xlsx", "invalid_format"), (run, "jsonl", "served")])
        self.assertNotIn(RESEARCHER, json.dumps(latest))
        self.assertEqual(send("GET", "/api/v1/admin/exports", token=TOKEN)[0], 401)


if __name__ == "__main__":
    unittest.main()
