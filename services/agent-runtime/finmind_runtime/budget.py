"""ADR-0019: a safe upper bound on a request's input tokens, for providers without a token-counting endpoint.

A text token covers at least one byte of UTF-8, so the byte length bounds the text's tokens. The fixed margin covers
tokens the service adds itself (chat formatting, tool definitions). It is an assumption until a live run confirms it.
The cost reservation uses this bound; the recorded cost comes from the provider's reported usage, so it stays exact.
"""

import json
from typing import Any

MARGIN_TOKENS = 1024
BOUND_VERSION = f"utf8-bytes+{MARGIN_TOKENS}/v1"


def request_bytes(request: Any) -> bytes:
    """The compact UTF-8 JSON an adapter sends; bytes and text are measured as they are."""
    if isinstance(request, bytes):
        return request
    if isinstance(request, str):
        return request.encode("utf-8")
    return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def conservative_input_tokens(request: Any) -> int:
    return len(request_bytes(request)) + MARGIN_TOKENS
