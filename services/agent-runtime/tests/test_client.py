import json
import unittest

import _path  # noqa: F401
from finmind_runtime.client import ApiError, Client, SessionEnded, Unreachable


class FakeTransport:
    def __init__(self, outcomes):
        self.outcomes = list(outcomes)
        self.requests = []

    def __call__(self, method, url, headers, body, timeout):
        self.requests.append((method, url, headers.get("Authorization"), body))
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        status, payload = outcome
        return status, b"" if payload is None else json.dumps(payload).encode()


def client(transport):
    c = Client("http://api", "w" * 32, transport, sleep=lambda _: None)
    c.session_token = "session"
    return c


class ClientTests(unittest.TestCase):
    def test_retries_identical_request_after_dropped_responses(self):
        transport = FakeTransport([TimeoutError(), (503, None), (200, {"status": "committed"})])
        response = client(transport).tool_call("turn-0003", "pay_bill", {"bill_id": "b", "idempotency_key": "k"})
        self.assertEqual(response["status"], "committed")
        self.assertEqual(len(transport.requests), 3)
        self.assertEqual(len({request[3] for request in transport.requests}), 1)

    def test_gives_up_after_retries(self):
        with self.assertRaises(Unreachable):
            client(FakeTransport([ConnectionError()] * 4)).heartbeat()

    def test_401_ends_session_and_4xx_raises(self):
        with self.assertRaises(SessionEnded):
            client(FakeTransport([(401, None)])).heartbeat()
        with self.assertRaises(ApiError) as error:
            client(FakeTransport([(400, {"reasonCode": "INVALID_RUN_CONFIG"})])).initialize()
        self.assertEqual(error.exception.reason_code, "INVALID_RUN_CONFIG")

    def test_claim_uses_worker_token_and_handles_empty_queue(self):
        transport = FakeTransport([(204, None)])
        self.assertIsNone(client(transport).claim())
        self.assertEqual(transport.requests[0][2], "Bearer " + "w" * 32)


if __name__ == "__main__":
    unittest.main()
