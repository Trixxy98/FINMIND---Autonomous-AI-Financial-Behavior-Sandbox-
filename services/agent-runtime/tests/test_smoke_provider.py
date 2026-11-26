"""ADR-0019 step 1: the smoke script refuses providers without an adapter before any request is made."""

import importlib.util
import io
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "smoke_provider.py"
spec = importlib.util.spec_from_file_location("smoke_provider", SCRIPT)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class SmokeProviderTests(unittest.TestCase):
    def test_a_provider_without_an_adapter_is_refused_without_any_request(self):
        with mock.patch.object(smoke, "http", side_effect=AssertionError("no request")), redirect_stdout(io.StringIO()) as out:
            self.assertEqual(smoke.main(["--provider", "someone-else"]), 2)
        self.assertIn("No adapter", out.getvalue())

    def test_missing_tokens_stop_before_any_request(self):
        with mock.patch.object(smoke, "TOKEN", ""), mock.patch.object(smoke, "http", side_effect=AssertionError("no request")), \
                redirect_stdout(io.StringIO()):
            self.assertEqual(smoke.main(["--provider", "anthropic"]), 2)

    def test_the_budget_stays_at_fifty_cents_and_only_providers_with_adapters_are_listed(self):
        self.assertEqual(smoke.MAX_COST_MICRO_USD, 500_000)
        self.assertEqual(sorted(smoke.ADAPTERS), ["anthropic", "groq", "openai"])

    def test_the_old_claude_script_still_runs_the_anthropic_smoke(self):
        text = (SCRIPT.parent / "smoke_claude.py").read_text(encoding="utf-8")
        self.assertIn('"--provider", "anthropic"', text)


if __name__ == "__main__":
    unittest.main()
