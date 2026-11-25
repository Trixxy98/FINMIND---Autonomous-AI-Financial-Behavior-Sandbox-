"""ADR-0017 step 3: the crash drill refuses anything but a test database. The drill itself runs as a CI step."""

import importlib.util
import io
import os
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "crash_drill.py"
spec = importlib.util.spec_from_file_location("crash_drill", SCRIPT)
crash_drill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(crash_drill)


class CrashDrillGuardTests(unittest.TestCase):
    def refused(self, database):
        with mock.patch.dict(os.environ, {"FINMIND_API_TEST_DATABASE": database}, clear=False), \
                mock.patch.object(crash_drill.subprocess, "Popen", side_effect=AssertionError("must not start")), \
                redirect_stdout(io.StringIO()) as out:
            return crash_drill.main(), out.getvalue()

    def test_only_a_database_named_for_tests_is_accepted(self):
        for database in ("", "Host=127.0.0.1;Database=finmind;Username=finmind_api",
                         "Host=127.0.0.1;Database=finmind_test_copy;Username=finmind_api", "Host=127.0.0.1"):
            code, out = self.refused(database)
            self.assertEqual(code, 2, database)
            self.assertIn("test database", out)

    def test_it_runs_mock_agents_only(self):
        self.assertIn('snapshot["agent"]["provider"] != "mock"', crash_drill.WORKER_CODE)


if __name__ == "__main__":
    unittest.main()
