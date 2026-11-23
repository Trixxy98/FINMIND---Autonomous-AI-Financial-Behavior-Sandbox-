"""ADR-0013 B2: the SSE event stream, against the real API and database."""

from http.client import HTTPConnection
import json
import threading
import time
import unittest

import _path  # noqa: F401
from finmind_runtime.client import Client
from test_end_to_end import API, RESEARCHER, TOKEN, create_and_start, events, http, run_until_terminal, test_worker


class Stream:
    """A minimal SSE reader over http.client, so the test controls the connection."""

    def __init__(self, run_id, last_event_id=None, query="", timeout=60):
        self.connection = HTTPConnection("127.0.0.1", 5080, timeout=timeout)
        headers = {"Authorization": "Bearer " + RESEARCHER, "Accept": "text/event-stream"}
        if last_event_id is not None:
            headers["Last-Event-ID"] = str(last_event_id)
        self.connection.request("GET", f"/api/v1/runs/{run_id}/stream{query}", headers=headers)
        self.response = self.connection.getresponse()
        self.raw = b""

    def messages(self):
        fields, comments = {}, []
        while True:
            line = self.response.readline()
            if not line:
                return
            self.raw += line
            line = line.decode().rstrip("\n")
            if line.startswith(":"):
                yield {"comment": line}
            elif line == "":
                if fields:
                    yield fields
                fields = {}
            else:
                name, _, value = line.partition(": ")
                fields[name] = value

    def close(self):
        self.connection.close()


def collect(stream, stop_after=None):
    received = []
    for message in stream.messages():
        if "comment" in message:
            continue
        received.append(message)
        if message.get("event") == "end" or (stop_after and len(received) >= stop_after):
            break
    return received


@unittest.skipUnless(API and len(TOKEN) >= 32 and len(RESEARCHER) >= 32,
                     "set FINMIND_E2E_API_URL, FINMIND_WORKER_TOKEN, and FINMIND_RESEARCHER_TOKEN")
class EventStreamTests(unittest.TestCase):
    def finished_run(self):
        run_id = create_and_start(scenario="S01")
        run_until_terminal(run_id)
        return run_id

    def test_a_finished_run_streams_every_event_then_ends(self):
        run_id = self.finished_run()
        stream = Stream(run_id)
        self.assertEqual(stream.response.getheader("Content-Type").split(";")[0], "text/event-stream")
        received = collect(stream)
        stream.close()
        evs = events(run_id)
        self.assertEqual([int(m["id"]) for m in received[:-1]], [e["sequence"] for e in evs])
        self.assertEqual([m["event"] for m in received[:-1]], [e["eventType"] for e in evs])
        self.assertEqual(json.loads(received[0]["data"]), evs[0])
        end = received[-1]
        self.assertEqual((end["event"], json.loads(end["data"])), ("end", {"status": "COMPLETED", "lastSequence": evs[-1]["sequence"]}))

    def test_resume_with_last_event_id_or_after_sequence(self):
        run_id = self.finished_run()
        evs = events(run_id)
        middle = evs[len(evs) // 2]["sequence"]
        for label, stream in (("header", Stream(run_id, last_event_id=middle)),
                              ("query", Stream(run_id, query=f"?afterSequence={middle}")),
                              ("header wins", Stream(run_id, last_event_id=middle, query="?afterSequence=1"))):
            with self.subTest(label):
                received = collect(stream)
                stream.close()
                self.assertEqual([int(m["id"]) for m in received[:-1]], [e["sequence"] for e in evs if e["sequence"] > middle])

    def test_live_run_with_a_reconnect_has_no_gap_or_duplicate(self):
        while test_worker().run_once() is not None:
            pass
        run_id = create_and_start(scenario="S02", model="finmind-mock-chaotic-v1", seed=4)
        first = Stream(run_id)
        worker = threading.Thread(target=lambda: run_until_terminal(run_id))
        worker.start()
        part = collect(first, stop_after=6)
        first.close()  # the client drops mid-run
        resumed = collect(Stream(run_id, last_event_id=part[-1]["id"]))
        worker.join()
        ids = [int(m["id"]) for m in part + resumed[:-1]]
        self.assertEqual(ids, [e["sequence"] for e in events(run_id)])
        self.assertEqual(resumed[-1]["event"], "end")

    def test_late_usage_after_cancel_arrives_before_end(self):
        run_id = create_and_start(scenario="S01")
        client = Client(API, TOKEN)
        while True:
            claim = client.claim()
            client.session_token = claim["sessionToken"]
            if claim["runId"] == run_id:
                break
            client.finish("failed", "test: cleared from the shared test queue")
        client.initialize()
        client.reserve_model_call(1, 1, 1000, 1000)  # a mock run has no cost, but the reservation is recorded
        http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test"})
        stream = Stream(run_id)
        threading.Timer(2, lambda: client.model_invocation(1, 10, 10, 1, "late")).start()
        received = collect(stream)
        stream.close()
        self.assertIn("MODEL_INVOCATION_COMPLETED", [m["event"] for m in received])
        self.assertEqual(received[-1]["event"], "end")

    def test_idle_stream_sends_heartbeats(self):
        run_id = create_and_start(scenario="S01")  # queued, nothing happens
        stream = Stream(run_id, timeout=30)
        started, beat = time.monotonic(), None
        for message in stream.messages():
            if "comment" in message:
                beat = time.monotonic() - started
                break
        stream.close()
        http("POST", f"/internal/runs/{run_id}/cancel", {"reason": "test"})
        self.assertIsNotNone(beat)
        self.assertLess(beat, 20)

    def test_errors_and_no_credentials_in_the_stream(self):
        import uuid
        connection = HTTPConnection("127.0.0.1", 5080, timeout=10)
        for path, headers, expected in ((f"/api/v1/runs/{uuid.uuid4()}/stream", {}, 404),
                                        ("/api/v1/runs/RUN/stream", {"Last-Event-ID": "x"}, 400),
                                        ("/api/v1/runs/RUN/stream?afterSequence=-1", {}, 400)):
            run_id = self.finished_run() if "RUN" in path else None
            connection.request("GET", path.replace("RUN", run_id or ""), headers={"Authorization": "Bearer " + RESEARCHER, **headers})
            response = connection.getresponse()
            response.read()
            self.assertEqual(response.status, expected, path)
        connection.close()
        run_id = self.finished_run()
        stream = Stream(run_id)
        collect(stream)
        stream.close()
        for secret in (TOKEN, RESEARCHER):
            self.assertNotIn(secret.encode(), stream.raw)


if __name__ == "__main__":
    unittest.main()
