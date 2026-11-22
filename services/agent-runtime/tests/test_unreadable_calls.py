"""ADR-0013 item 18: every tool call the gateway cannot read is recorded as malformed."""

import hashlib
import json
import os
import random
import unittest

import _path  # noqa: F401
from finmind_runtime.agents import Decision
from finmind_runtime.client import Client, urllib_transport
from finmind_runtime.worker import Worker

API = os.environ.get("FINMIND_E2E_API_URL")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")
PATH = "/internal/worker/session/tool-calls"


def nested(depth):
    value = {}
    for _ in range(depth):
        value = {"a": value}
    return value


def envelope(call_id, arguments="{}", tool='"submit_plan"'):
    return f'{{"toolCallId":"{call_id}","tool":{tool},"arguments":{arguments}}}'.encode()


class ClientTests(unittest.TestCase):
    def test_arguments_that_cannot_be_serialised_are_still_sent_as_an_attempt(self):
        for label, arguments in (("recursion", nested(100_000)), ("huge integer", {"n": 10 ** 5000})):
            with self.subTest(label):
                sent = []
                client = Client("http://api", "w" * 32, lambda *args: (sent.append(json.loads(args[3])), (200, b"{}"))[1])
                client.session_token = "s"
                client.tool_call("turn-0001", "submit_plan", arguments)
                self.assertEqual(list(sent[0]["arguments"]), ["_unserializable_arguments"])


@unittest.skipUnless(API and len(TOKEN) >= 32, "set FINMIND_E2E_API_URL and FINMIND_WORKER_TOKEN")
class GatewayTests(unittest.TestCase):
    def claimed(self):
        from test_end_to_end import config, http
        run_id = http("POST", "/internal/runs", config(max_actions=1000))["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        client = Client(API, TOKEN)
        for _ in range(1000):
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                client.initialize()
                return client, run_id
            client.finish("failed", "test: cleared from the shared test queue")
        self.fail("run was not claimed")

    def post(self, client, body):
        status, raw = urllib_transport("POST", API + PATH, {"Authorization": "Bearer " + client.session_token,
                                                            "Content-Type": "application/json"}, body, 10)
        return status, json.loads(raw) if raw else {}

    def test_unreadable_bodies_are_recorded_with_size_and_hash(self):
        from test_end_to_end import events
        deep = json.dumps(nested(70))
        cases = {
            "c-deep": envelope("c-deep", deep),
            "c-depth-50": envelope("c-depth-50", json.dumps({"text": nested(50)})),
            "c-infinity": envelope("c-infinity", '{"amount_sen":Infinity}'),
            "c-overflow": envelope("c-overflow", '{"amount_sen":1e200000}'),
            "c-underflow": envelope("c-underflow", '{"amount_sen":1e-20000}'),
            "c-duplicate": envelope("c-duplicate", '{"text":"a","text":"b"}'),
            "c-nested-duplicate": envelope("c-nested-duplicate", '{"text":{"a":1,"a":2}}'),
            "c-control-name": envelope("c-control-name", tool='"\\u0001"'),
            "c-empty-name": envelope("c-empty-name", tool='""'),
            "c-long-name": envelope("c-long-name", tool='"' + "n" * 300 + '"'),
            "c-number-name": envelope("c-number-name", tool="7"),
            "c-large": envelope("c-large", json.dumps({"text": "x" * 40_000})),
            "c-cut-short": envelope("c-cut-short")[:-1],
            "c-extra-key": b'{"toolCallId":"c-extra-key","tool":"get_balance","arguments":{},"run":"other"}',
        }
        without_id = [b"", b"not json", b"[]", b"null", b'{"toolCallId":7,"tool":"get_balance"}', b"\xff\xfe\x00"]
        client, run_id = self.claimed()
        for label, body in [*cases.items(), *((str(body), body) for body in without_id)]:
            with self.subTest(label):
                status, response = self.post(client, body)
                self.assertEqual((status, response.get("status"), response.get("committed")), (200, "malformed", False))
                self.assertEqual(self.post(client, body), (status, response))  # a retry replays

        recorded = events(run_id)  # must stay readable
        proposed = {e["traceId"]: e["payload"] for e in recorded if e["eventType"] == "TOOL_CALL_PROPOSED"}
        self.assertEqual(len(proposed), len(cases) + len(without_id))
        for call_id, body in cases.items():
            with self.subTest(call_id):
                kept = proposed[call_id]["arguments"]["_unreadable"]
                self.assertEqual((kept["bytes"], kept["sha256"]), (len(body), hashlib.sha256(body).hexdigest()))
        self.assertEqual(self.post(client, envelope("c-deep", json.dumps(nested(80))))[1]["reason_code"],
                         "TOOL_CALL_ID_CONFLICT")
        client.finish("completed", "test")

    def test_readable_calls_are_untouched(self):
        client, _ = self.claimed()
        self.assertEqual(client.tool_call("r-1", "submit_plan", {"text": "a plan"})["status"], "ok")
        self.assertEqual(client.tool_call("r-2", "no_such_tool", {})["status"], "unsupported_capability")
        self.assertEqual(client.tool_call("r-3", "move_to_savings", {"amount_sen": 10 ** 30, "idempotency_key": "k"})["status"],
                         "malformed")
        self.assertEqual(client.tool_call("r-4", "submit_plan", {"text": nested(8)})["data"]["fields"], ["text"])
        client.finish("completed", "test")

    def test_hostile_bodies_never_escape_the_record(self):
        from test_end_to_end import http
        rng = random.Random(18)
        pieces = ["{", "}", "[", "]", '"', "\\", ",", ":", "1e999", "-", "0", "9" * 30, "NaN", "null", "\\u0000",
                  "\\ud800", "\x00", "\x7f", "é", "\U0001F600", " ", '"a":1', '"a":1,"a":2', "sk-ant-aaaaaaaaaa"]
        client, run_id = self.claimed()
        for number in range(400):
            noise = "".join(rng.choice(pieces) for _ in range(rng.randint(0, 14)))
            shape = rng.randrange(6)
            call_id = f"f-{number:04d}"
            body = [lambda: envelope(call_id, noise or "{}"),
                    lambda: envelope(call_id, json.dumps({"text": noise})),
                    lambda: envelope(call_id, tool=json.dumps(noise)),
                    lambda: envelope(call_id, json.dumps({noise: [noise]})),
                    lambda: envelope(call_id, json.dumps(nested(rng.randint(0, 90)))),
                    lambda: (noise + str(number)).encode()][shape]()
            status, response = self.post(client, body)
            self.assertEqual(status, 200, body)
            self.assertIn(response["status"], {"ok", "malformed", "unsupported_capability"}, body)
        seen, after = 0, 0
        while True:
            page = http("GET", f"/internal/runs/{run_id}/events?limit=500&afterSequence={after}")
            if not page["events"]:
                break
            seen += sum(e["eventType"] == "TOOL_RETURNED" for e in page["events"])
            after = page["nextAfterSequence"]
        self.assertEqual(seen, 400)
        self.assertTrue(http("GET", f"/internal/runs/{run_id}/events/verify")["intact"])
        client.finish("completed", "test")

    def test_agent_run_survives_unreadable_calls(self):
        from test_end_to_end import config, http, run_until_terminal, test_worker
        while test_worker().run_once() is not None:  # leave only this test's run in the queue
            pass
        turns = [("submit_plan", {"text": nested(70)}),
                 ("move_to_savings", {"amount_sen": float("inf"), "idempotency_key": "k"}),
                 ("\x01", {}), ("finish", {"summary": "done"})]

        class Agent:
            def next_action(self, observation):
                return Decision(*turns[observation.turn - 1])

        run_id = http("POST", "/internal/runs", config())["runId"]
        http("POST", f"/internal/runs/{run_id}/start")
        run_until_terminal(run_id, Worker(Client(API, TOKEN), agent_factory=lambda *args: Agent()))
        self.assertEqual(http("GET", f"/internal/runs/{run_id}")["status"], "COMPLETED")


if __name__ == "__main__":
    unittest.main()
