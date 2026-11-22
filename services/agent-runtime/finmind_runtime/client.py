"""HTTP client for the ADR-0009 worker interface, with ADR-0011 retries.

Retries resend the identical request (same toolCallId); the gateway replays completed calls and resumes
interrupted ones, so retries never duplicate an economic effect.
"""

from __future__ import annotations

import json
import socket
import time
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, Optional, Tuple

RETRY_DELAYS = (0.5, 1.0, 2.0)
TIMEOUT_SECONDS = 10.0
# ADR-0013 A1: stay inside the gateway's limits, so a record is never lost to a rejected request.
MAX_TOOL_CALL_BYTES = 16_384
MAX_TOOL_NAME_CHARS = 100
MAX_OUTPUT_TEXT_UNITS = 90_000  # UTF-16 code units, as the server counts
MAX_TOOL_USE_COUNT = 100


class SessionEnded(Exception):
    """HTTP 401: the session expired or the run ended."""


class ApiError(Exception):
    def __init__(self, status: int, body: Any) -> None:
        super().__init__(f"API returned HTTP {status}")
        self.status = status
        self.body = body

    @property
    def reason_code(self) -> Optional[str]:
        if isinstance(self.body, dict):
            return self.body.get("reasonCode") or self.body.get("reason_code")
        return None


class Unreachable(Exception):
    """The API could not be reached after all retries."""


Transport = Callable[[str, str, Dict[str, str], Optional[bytes], float], Tuple[int, bytes]]


def urllib_transport(method: str, url: str, headers: Dict[str, str], body: Optional[bytes], timeout: float) -> Tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        with error:
            return error.code, error.read()


class Client:
    def __init__(self, base_url: str, worker_token: str, transport: Transport = urllib_transport,
                 sleep: Callable[[float], None] = time.sleep) -> None:
        self._base = base_url.rstrip("/")
        self._worker_token = worker_token
        self._transport = transport
        self._sleep = sleep
        self.session_token: Optional[str] = None

    # Worker credential: claim only.
    def claim(self) -> Optional[Dict[str, Any]]:
        status, body = self._send("POST", "/internal/worker/runs/claim", self._worker_token, None)
        return None if status == 204 else body

    # Session credential.
    def initialize(self) -> Dict[str, Any]:
        return self._session("/internal/worker/session/initialize", None)

    def heartbeat(self) -> Dict[str, Any]:
        return self._session("/internal/worker/session/heartbeat", None)

    def model_invocation(self, turn: int, input_tokens: int, output_tokens: int, latency_ms: int, output_text: str,
                         **extra: Any) -> Dict[str, Any]:
        """extra: cacheReadInputTokens, cacheCreationInputTokens, model, stopReason, refusalCategory, toolUseCount."""
        encoded = output_text.encode("utf-16-le", "replace")
        if len(encoded) > 2 * MAX_OUTPUT_TEXT_UNITS:
            output_text = encoded[:2 * MAX_OUTPUT_TEXT_UNITS].decode("utf-16-le", "ignore")
        payload = {"turn": turn, "inputTokens": input_tokens, "outputTokens": output_tokens,
                   "latencyMs": min(latency_ms, 3_600_000), "outputText": output_text}
        if extra.get("toolUseCount") is not None:
            extra["toolUseCount"] = min(extra["toolUseCount"], MAX_TOOL_USE_COUNT)
        payload.update({key: value for key, value in extra.items() if value is not None})
        return self._session("/internal/worker/session/model-invocations", payload)

    def tool_call(self, tool_call_id: str, tool: str, arguments: Any) -> Dict[str, Any]:
        payload = {"toolCallId": tool_call_id, "tool": tool[:MAX_TOOL_NAME_CHARS] or "(empty)", "arguments": arguments}
        size = len(json.dumps(payload).encode("utf-8"))
        if size > MAX_TOOL_CALL_BYTES:
            # No tool accepts this field, so the gateway records the attempt as malformed and the agent is told.
            payload["arguments"] = {"_oversized_arguments_bytes": size}
        return self._session("/internal/worker/session/tool-calls", payload)

    def reserve_model_call(self, turn: int, attempt: int, input_tokens: int, max_output_tokens: int) -> Dict[str, Any]:
        """ADR-0013 A1: must succeed before a paid request is sent. A retry returns the same reservation."""
        return self._session("/internal/worker/session/model-reservations",
                             {"turn": turn, "attempt": attempt, "inputTokens": input_tokens,
                              "maxOutputTokens": max_output_tokens})

    def finish(self, outcome: str, reason: str) -> Dict[str, Any]:
        return self._session("/internal/worker/session/finish", {"outcome": outcome, "reason": reason})

    def _session(self, path: str, payload: Optional[Dict[str, Any]]) -> Dict[str, Any]:
        if not self.session_token:
            raise SessionEnded()
        _, body = self._send("POST", path, self.session_token, payload)
        return body

    def _send(self, method: str, path: str, token: str, payload: Optional[Dict[str, Any]]) -> Tuple[int, Any]:
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        headers = {"Authorization": "Bearer " + token, "Content-Type": "application/json"}
        last_error: Optional[BaseException] = None
        for attempt in range(len(RETRY_DELAYS) + 1):
            if attempt:
                self._sleep(RETRY_DELAYS[attempt - 1])
            try:
                status, raw = self._transport(method, self._base + path, headers, data, TIMEOUT_SECONDS)
            except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError, OSError) as error:
                last_error = error
                continue
            if status >= 500:
                last_error = ApiError(status, None)
                continue
            if status == 401:
                raise SessionEnded()
            try:
                body = json.loads(raw) if raw else None
            except ValueError:
                raise ApiError(status, None) from None
            if status >= 400:
                raise ApiError(status, body)
            return status, body
        raise Unreachable(f"{method} {path} failed after retries: {type(last_error).__name__}")
