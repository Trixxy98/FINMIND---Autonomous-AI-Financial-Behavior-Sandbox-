import json
import sys
import unittest
from pathlib import Path
from http.server import HTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from threading import Thread
from urllib.request import urlopen

from health_server import HealthHandler


class HealthEndpointTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), HealthHandler)
        cls.thread = Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.url = "http://127.0.0.1:%d/health" % cls.server.server_port

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def test_health_returns_service_status(self):
        with urlopen(self.url) as response:
            self.assertEqual(response.status, 200)
            payload = json.load(response)
        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["service"], "finmind-agent-runtime")
        self.assertEqual(payload["phase"], 0)


if __name__ == "__main__":
    unittest.main()
