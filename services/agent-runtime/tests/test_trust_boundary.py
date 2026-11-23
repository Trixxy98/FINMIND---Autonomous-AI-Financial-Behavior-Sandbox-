"""ADR-0013 A3: researcher routes need the researcher credential, a local Host, and no foreign Origin.

Run against the real API. Also: the worker refuses database credentials in its environment.
"""

import http.client
import json
import os
import unittest
import uuid
from unittest import mock

import _path  # noqa: F401
from finmind_runtime import worker
from finmind_runtime.client import Client

API = os.environ.get("FINMIND_E2E_API_URL")
WORKER_TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")
RESEARCHER = os.environ.get("FINMIND_RESEARCHER_TOKEN", "")
LOCAL = "127.0.0.1:5080"
RUN = str(uuid.uuid4())
READS = ["/internal/scenarios", f"/internal/runs/{RUN}", f"/internal/runs/{RUN}/events",
         f"/internal/runs/{RUN}/events/verify", f"/internal/runs/{RUN}/environment",
         f"/internal/runs/{RUN}/accounts/{RUN}/balance", f"/internal/runs/{RUN}/obligations/{RUN}"]
WRITES = ["/internal/runs", f"/internal/runs/{RUN}/start", f"/internal/runs/{RUN}/cancel", f"/internal/runs/{RUN}/funding",
          f"/internal/runs/{RUN}/accounts", f"/internal/runs/{RUN}/transfers", f"/internal/runs/{RUN}/savings",
          f"/internal/runs/{RUN}/bill-payments", f"/internal/runs/{RUN}/obligations",
          f"/internal/runs/{RUN}/journals/{RUN}/reversals"]


def send(method, path, token=None, host=LOCAL, origin=None, body=None):
    connection = http.client.HTTPConnection("127.0.0.1", 5080, timeout=10)
    headers = {"Host": host}
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    if origin is not None:
        headers["Origin"] = origin
    if body is not None:
        headers["Content-Type"] = "application/json"
    connection.request(method, path, body=None if body is None else json.dumps(body), headers=headers)
    response = connection.getresponse()
    raw = response.read()
    connection.close()
    return response.status, raw


class WorkerEnvironmentTests(unittest.TestCase):
    def test_worker_refuses_database_and_researcher_credentials(self):
        for name in ("ConnectionStrings__Finmind", "FINMIND_TEST_DATABASE", "FINMIND_API_TEST_DATABASE",
                     "POSTGRES_PASSWORD", "PGPASSWORD", "FINMIND_RESEARCHER_TOKEN", "SOME_URL"):
            value = "Host=db;Username=finmind;Password=secret-value" if name == "SOME_URL" else "secret-value"
            with self.subTest(name), mock.patch.dict(os.environ, {"FINMIND_WORKER_TOKEN": "w" * 32, name: value}, clear=True), \
                    mock.patch.object(worker, "Client", side_effect=AssertionError("must not start")), \
                    self.assertLogs("finmind_runtime", "ERROR") as logged:
                self.assertEqual(worker.main(["--once"]), 2)
                self.assertNotIn("secret-value", "\n".join(logged.output))


@unittest.skipUnless(API and len(WORKER_TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class ResearcherRouteTests(unittest.TestCase):
    def each_route(self):
        for path in READS:
            yield "GET", path
        for path in WRITES:
            yield "POST", path

    def test_routes_reject_missing_or_wrong_credentials(self):
        client = Client(API, WORKER_TOKEN)
        session = (client.claim() or {}).get("sessionToken")  # any queued run will do; it is left to expire
        wrong = {"none": None, "wrong": "x" * 48, "worker": WORKER_TOKEN, "researcher with suffix": RESEARCHER + "x"}
        if session:
            wrong["session"] = session
        for method, path in self.each_route():
            for label, token in wrong.items():
                with self.subTest(route=f"{method} {path}", token=label):
                    status, raw = send(method, path, token, body={} if method == "POST" else None)
                    self.assertEqual(status, 401)
                    self.assertNotIn(WORKER_TOKEN.encode(), raw)

    def test_foreign_host_and_origin_are_rejected_even_with_the_credential(self):
        cases = {"rebinding host": {"host": "attacker.example:5080"}, "host without port": {"host": "127.0.0.1"},
                 "foreign origin": {"origin": "http://attacker.example"}, "null origin": {"origin": "null"},
                 "other local port": {"origin": "http://127.0.0.1:8000"}}
        for method, path in self.each_route():
            for label, extra in cases.items():
                with self.subTest(route=f"{method} {path}", case=label):
                    status, _ = send(method, path, RESEARCHER, body={} if method == "POST" else None, **extra)
                    self.assertEqual(status, 403)

    def test_health_and_worker_routes_check_the_host_too(self):
        self.assertEqual(send("GET", "/health", host="attacker.example:5080")[0], 403)
        self.assertEqual(send("POST", "/internal/worker/runs/claim", WORKER_TOKEN, host="attacker.example:5080")[0], 403)
        self.assertEqual(send("GET", "/health")[0], 200)

    def test_the_researcher_credential_works_and_opens_nothing_else(self):
        self.assertEqual(send("GET", "/internal/scenarios", RESEARCHER)[0], 200)
        self.assertEqual(send("GET", "/internal/scenarios", RESEARCHER, host="localhost:5080")[0], 200)
        self.assertEqual(send("GET", "/internal/scenarios", RESEARCHER, origin="http://127.0.0.1:5173")[0], 200)
        self.assertEqual(send("POST", "/internal/worker/runs/claim", RESEARCHER)[0], 401)
        self.assertEqual(send("POST", "/INTERNAL/WORKER/runs/claim", RESEARCHER)[0], 401)
        self.assertEqual(send("GET", "/Internal/Scenarios")[0], 401)


if __name__ == "__main__":
    unittest.main()
