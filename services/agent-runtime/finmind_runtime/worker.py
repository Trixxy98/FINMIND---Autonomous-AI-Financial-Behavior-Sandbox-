"""Worker loop (ADR-0011): claim → initialize → turns (heartbeat, decide, record invocation, call tool) → finish.

Run: python3 -m finmind_runtime.worker [--once]   (from services/agent-runtime)
Environment: FINMIND_API_URL (default http://127.0.0.1:5080), FINMIND_WORKER_TOKEN. Tokens are never logged.
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import time
from dataclasses import dataclass
from typing import Any, Callable, Dict, Optional

from .agents import Agent, Observation, create_agent
from .client import ApiError, Client, SessionEnded, Unreachable

log = logging.getLogger("finmind_runtime")

# Server responses that mean the run has ended; the server already moved it and revoked the session.
TERMINAL_REASONS = {"RUN_NOT_ACTIVE", "ACTION_BUDGET_EXCEEDED", "WALL_TIME_BUDGET_EXCEEDED", "TOKEN_BUDGET_EXCEEDED"}


@dataclass
class RunResult:
    run_id: str
    outcome: str        # completed | failed | budget_exceeded | stopped
    turns: int
    reason: str


def tool_call_id(turn: int) -> str:
    return f"turn-{turn:04d}"


class Worker:
    def __init__(self, client: Client, agent_factory: Callable[[Dict[str, Any]], Agent] = create_agent,
                 clock: Callable[[], float] = time.monotonic,
                 on_turn: Optional[Callable[[str, int], None]] = None) -> None:
        self._client = client
        self._agent_factory = agent_factory
        self._clock = clock
        self._on_turn = on_turn

    def run_once(self) -> Optional[RunResult]:
        claim = self._client.claim()
        if claim is None:
            return None
        self._client.session_token = claim["sessionToken"]
        try:
            return self._run(claim)
        finally:
            self._client.session_token = None

    def _run(self, claim: Dict[str, Any]) -> RunResult:
        run_id = claim["runId"]
        snapshot = claim["configSnapshot"]
        log.info("claimed run %s (%s)", run_id, snapshot.get("scenarioVersion"))
        try:
            self._client.initialize()
            agent = self._agent_factory(snapshot)
        except ApiError as error:
            return self._finish(run_id, "failed", f"runtime: initialization rejected ({error.reason_code})", 0)
        except ValueError as error:
            return self._finish(run_id, "failed", f"runtime: {error}", 0)
        except SessionEnded:
            return RunResult(run_id, "stopped", 0, "session ended")

        deadline = self._clock() + int(snapshot["maxWallTimeSeconds"])
        # The server enforces maxActions (ADR-0009); this is only a safety net one turn beyond it.
        max_turns = int(snapshot["maxActions"]) + 1
        last_tool: Optional[str] = None
        last_response: Optional[Dict[str, Any]] = None
        turn = 0
        try:
            while True:
                if self._clock() >= deadline:
                    return self._finish(run_id, "budget_exceeded", "runtime: wall-time budget reached", turn)
                if turn >= max_turns:
                    return self._finish(run_id, "budget_exceeded", "runtime: action budget reached", turn)
                status = self._client.heartbeat().get("status")
                if status != "RUNNING":
                    return RunResult(run_id, "stopped", turn, f"run is {status}")

                turn += 1
                if self._on_turn:
                    self._on_turn(run_id, turn)
                decision = agent.next_action(Observation(snapshot["objective"], claim.get("tools", []), turn, last_tool, last_response))
                invocation = self._client.model_invocation(turn, decision.input_tokens, decision.output_tokens,
                                                           decision.latency_ms, decision.output_text)
                if invocation.get("reason_code") in TERMINAL_REASONS:
                    return RunResult(run_id, "stopped", turn, invocation["reason_code"])

                response = self._client.tool_call(tool_call_id(turn), decision.tool, decision.arguments)
                last_tool, last_response = decision.tool, response
                reason = response.get("reason_code")
                if reason in TERMINAL_REASONS:
                    return RunResult(run_id, "stopped", turn, reason)
                # After the virtual duration ends, mock agents read their balance and call finish.
                if decision.tool == "finish" and response.get("status") == "ok":
                    return self._finish(run_id, "completed", "agent finished", turn)
        except SessionEnded:
            return RunResult(run_id, "stopped", turn, "session ended")
        except Unreachable:
            return self._finish(run_id, "failed", "runtime: API unreachable", turn)

    def _finish(self, run_id: str, outcome: str, reason: str, turns: int) -> RunResult:
        try:
            self._client.finish(outcome, reason)
        except (SessionEnded, ApiError, Unreachable) as error:
            log.warning("finish for run %s not accepted: %s", run_id, type(error).__name__)
        log.info("run %s finished: %s (%s) after %d turns", run_id, outcome, reason, turns)
        return RunResult(run_id, outcome, turns, reason)


def main(argv: Optional[list] = None) -> int:
    parser = argparse.ArgumentParser(description="FINMIND agent runtime worker")
    parser.add_argument("--once", action="store_true", help="claim one run, run it, and exit")
    parser.add_argument("--poll-seconds", type=float, default=2.0)
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    token = os.environ.get("FINMIND_WORKER_TOKEN", "")
    if len(token) < 32:
        log.error("FINMIND_WORKER_TOKEN must be set (at least 32 characters).")
        return 2
    worker = Worker(Client(os.environ.get("FINMIND_API_URL", "http://127.0.0.1:5080"), token))
    while True:
        result = worker.run_once()
        if args.once:
            return 0 if result is None or result.outcome != "failed" else 1
        if result is None:
            time.sleep(args.poll_seconds)


if __name__ == "__main__":
    sys.exit(main())
