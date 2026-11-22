"""ADR-0013 items 16 and 17 through the real API: invalid agent Unicode, and cancellation after a commit."""

import json
import os
import unittest

import _path  # noqa: F401
from finmind_runtime.client import Client, SessionEnded, cut, urllib_transport

API = os.environ.get("FINMIND_E2E_API_URL")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")
LONE = "\ud800"
REPLACED = "�"


class CutTests(unittest.TestCase):
    def test_cutting_leaves_invalid_units_for_the_gateway_to_judge(self):
        self.assertEqual(cut("a" + LONE + "b" * 100, 100), "a" + LONE + "b" * 98)


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN")
class GatewayTests(unittest.TestCase):
    def claimed(self, transport=urllib_transport):
        from test_end_to_end import config, http
        run_id = http("POST", "/internal/runs", config(scenario="S02"))["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        client = Client(API, TOKEN, transport=transport, sleep=lambda _: None)
        for _ in range(1000):
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                client.initialize()
                return client, run_id
            client.finish("failed", "test: cleared from the shared test queue")
        self.fail("run was not claimed")

    def test_invalid_unicode_is_recorded_as_malformed_and_the_run_goes_on(self):
        from test_end_to_end import events
        cases = {
            "text": ("submit_plan", {"text": f"x{LONE}y"}),
            "low-surrogate": ("submit_plan", {"text": "x\udc00y"}),
            "idempotency-key": ("move_to_savings", {"amount_sen": 1, "idempotency_key": f"k{LONE}"}),
            "tool-name": (f"to{LONE}ol", {}),
            "argument-name": ("submit_plan", {"text": "x", LONE: 1}),
            "colliding-names": ("submit_plan", {"\ud800": 1, "\udc00": 2}),
            "nested": ("submit_plan", {"text": [{"deep": LONE}]}),
        }
        client, run_id = self.claimed()
        for number, (label, (tool, arguments)) in enumerate(cases.items(), start=1):
            with self.subTest(label):
                response = client.tool_call(f"turn-{number:04d}", tool, arguments)
                self.assertEqual((response["status"], response["reason_code"]), ("malformed", "INVALID_TOOL_ARGUMENTS"))
                self.assertFalse(response["committed"])
                self.assertEqual(client.tool_call(f"turn-{number:04d}", tool, arguments), response)  # a retry replays
        valid = client.tool_call("turn-0100", "submit_plan",
                                 {"text": f"genuine {REPLACED}, emoji \U0001F600, literal \\ud800"})
        self.assertEqual(valid["status"], "ok")
        recorded = events(run_id)
        proposed = [e for e in recorded if e["eventType"] == "TOOL_CALL_PROPOSED"]
        self.assertEqual(len(proposed), len(cases) + 1)
        self.assertEqual(proposed[0]["payload"]["arguments"]["text"], f"x{REPLACED}y")
        client.finish("completed", "test")

    def test_same_id_with_repaired_text_is_not_the_same_request(self):
        client, _ = self.claimed()
        client.tool_call("turn-0001", "submit_plan", {"text": REPLACED})
        again = client.tool_call("turn-0001", "submit_plan", {"text": LONE})
        self.assertEqual(again["reason_code"], "TOOL_CALL_ID_CONFLICT")
        client.finish("completed", "test")

    def test_invalid_output_text_does_not_lose_the_usage_record(self):
        from test_end_to_end import events
        client, run_id = self.claimed()
        self.assertEqual(client.model_invocation(1, 0, 0, 1, f"x{LONE}y")["status"], "ok")
        recorded = [e["payload"]["outputText"] for e in events(run_id) if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
        self.assertEqual(recorded, [f"x{REPLACED}y"])
        client.finish("completed", "test")

    def test_invalid_raw_bytes_are_recorded_as_malformed(self):
        client, _ = self.claimed()
        body = b'{"toolCallId":"turn-0001","tool":"submit_plan","arguments":{"text":"x\xffy"}}'
        status, raw = urllib_transport("POST", API + "/internal/worker/session/tool-calls",
                                       {"Authorization": "Bearer " + client.session_token, "Content-Type": "application/json"},
                                       body, 10)
        self.assertEqual((status, json.loads(raw)["status"]), (200, "malformed"))
        client.finish("completed", "test")

    def test_lost_response_then_cancel_keeps_the_committed_record(self):
        from test_end_to_end import events, http
        cancel = []

        def losing(method, url, headers, body, timeout):
            status, raw = urllib_transport(method, url, headers, body, timeout)
            if body and b'"move_to_savings"' in body and not cancel:
                cancel.append(True)
                http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test"})
                raise TimeoutError("response lost")
            return status, raw

        client, run_id = self.claimed(losing)
        with self.assertRaises(SessionEnded):
            client.tool_call("turn-0001", "move_to_savings", {"amount_sen": 1000, "idempotency_key": "save"})
        returned = [e["outcome"] for e in events(run_id) if e["eventType"] == "TOOL_RETURNED"]
        self.assertEqual(returned, ["committed"])


if __name__ == "__main__":
    unittest.main()
