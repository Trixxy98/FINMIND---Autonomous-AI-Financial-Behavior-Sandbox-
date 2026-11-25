"""ADR-0017 step 6: the demo touches only its own database, through the restricted role."""

import importlib.util
import unittest
from pathlib import Path

import _path  # noqa: F401

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "demo.py"
spec = importlib.util.spec_from_file_location("demo", SCRIPT)
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)


class DemoSafetyTests(unittest.TestCase):
    def test_the_api_connection_is_pointed_at_the_demo_database(self):
        for given in ("Host=127.0.0.1;Port=5432;Database=finmind;Username=finmind_api;Password=x",
                      "Host=127.0.0.1;Database = finmind_test ;Username=finmind_api;Password=x",
                      "Host=127.0.0.1;Username=finmind_api;Password=x"):
            result = demo.demo_connection(given)
            self.assertRegex(result, r"(^|;)\s*Database\s*=\s*finmind_demo\s*(;|$)", given)
            self.assertNotRegex(result, r"Database\s*=\s*finmind(_test)?\s*(;|$)", given)

    def test_only_the_restricted_role_is_accepted(self):
        for given in ("Host=127.0.0.1;Database=finmind;Username=finmind;Password=x",
                      "Host=127.0.0.1;Database=finmind;Username=postgres;Password=x",
                      "Host=127.0.0.1;Database=finmind;Username=finmind_api_x;Password=x"):
            with self.assertRaises(SystemExit):
                demo.demo_connection(given)

    def test_only_a_database_it_created_may_be_dropped(self):
        self.assertTrue(demo.may_drop(False, None))
        self.assertTrue(demo.may_drop(True, demo.MARKER))
        self.assertFalse(demo.may_drop(True, None))
        self.assertFalse(demo.may_drop(True, "someone else's database"))
        self.assertEqual(demo.DATABASE, "finmind_demo")

    def test_environment_files_are_read_without_quotes(self):
        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".env", delete=False) as f:
            f.write('# comment\nA=1\nB="two words"\nC=\'x=y\'\n\n')
        self.assertEqual(demo.read_env(f.name), {"A": "1", "B": "two words", "C": "x=y"})
        Path(f.name).unlink()

    def test_it_runs_mock_agents_only_and_the_sample_configs_are_mock(self):
        import json
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('snapshot["agent"]["provider"] != "mock"', source)
        configs = json.loads((demo.ROOT / "samples" / "configs.json").read_text(encoding="utf-8"))["runs"]
        self.assertEqual({c["config"]["agent"]["provider"] for c in configs}, {"mock"})


if __name__ == "__main__":
    unittest.main()
