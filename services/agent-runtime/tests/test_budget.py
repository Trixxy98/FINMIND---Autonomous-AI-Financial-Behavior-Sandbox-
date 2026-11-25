"""ADR-0019 step 1: a safe upper bound on input tokens for providers without a token-counting endpoint."""

import unittest

import _path  # noqa: F401
from finmind_runtime.budget import BOUND_VERSION, MARGIN_TOKENS, conservative_input_tokens


class ConservativeBoundTests(unittest.TestCase):
    def test_the_bound_is_the_utf8_byte_length_plus_a_fixed_margin(self):
        self.assertEqual(BOUND_VERSION, f"utf8-bytes+{MARGIN_TOKENS}/v1")
        self.assertEqual(conservative_input_tokens(b"abc"), 3 + MARGIN_TOKENS)
        self.assertEqual(conservative_input_tokens("abc"), 3 + MARGIN_TOKENS)
        # Objects are measured as the compact UTF-8 JSON the adapters send: "é" is two bytes.
        self.assertEqual(conservative_input_tokens({"a": "é"}), len('{"a":"é"}'.encode("utf-8")) + MARGIN_TOKENS)

    def test_it_never_falls_below_the_characters_or_the_bytes(self):
        for text in ("Bayar bil elektrik RM180.00", "emoji 🙂 and CJK 日本", "", " " * 5000):
            bound = conservative_input_tokens(text)
            self.assertGreaterEqual(bound, len(text.encode("utf-8")))
            self.assertGreaterEqual(bound, len(text))
            self.assertGreaterEqual(bound, MARGIN_TOKENS)

    def test_a_request_object_is_measured_as_compact_json(self):
        request = {"model": "m", "messages": [{"role": "user", "content": "hello"}], "tools": []}
        compact = b'{"model":"m","messages":[{"role":"user","content":"hello"}],"tools":[]}'
        self.assertEqual(conservative_input_tokens(request), len(compact) + MARGIN_TOKENS)


if __name__ == "__main__":
    unittest.main()
