"""Worker loop (ADR-0011): claim → initialize → turns (heartbeat, decide, record invocation, call tool) → finish.

Run: python3 -m finmind_runtime.worker [--once]   (from services/agent-runtime)
Environment: FINMIND_API_URL (default http://127.0.0.1:5080), FINMIND_WORKER_TOKEN. Tokens are never logged.
"""

from __future__ import annotations

import argparse
import contextlib
import logging
import os
import sys
import threading
import time
from dataclasses import dataclass
from typing import Any, Callable, Dict, Iterator, List, Optional, Tuple

from .agents import Agent, Observation, create_agent
from .claude import ModelApiError, PromptMismatch, WallTimeReached
from .client import ApiError, Client, SessionEnded, Unreachable

log = logging.getLogger("finmind_runtime")

# Server responses that mean the run has ended; the server already moved it and revoked the session.
TERMINAL_REASONS = {"RUN_NOT_ACTIVE", "ACTION_BUDGET_EXCEEDED", "WALL_TIME_BUDGET_EXCEEDED", "TOKEN_BUDGET_EXCEEDED",
                    "COST_BUDGET_EXCEEDED"}
SESSION_RENEW_SECONDS = 60.0  # sessions last 10 minutes; a model call can last as long (ADR-0013 A1)
RENEW_GRACE_SECONDS = 60.0    # past the deadline a hung worker stops renewing and the server fails the run
MAX_IDLE_TURNS = 3
UNREPRESENTABLE_REASON = "unrepresentable tool input"  # ADR-0013 item 19; reported separately from other failures  # consecutive turns without tool calls before the run ends (ADR-0012)


class ReservationDenied(Exception):
    def __init__(self, reason_code: Optional[str]) -> None:
        super().__init__(reason_code)
        self.reason_code = reason_code


@dataclass
class RunResult:
    run_id: str
    outcome: str        # completed | failed | budget_exceeded | stopped
    turns: int
    reason: str


def tool_call_id(turn: int) -> str:
    return f"turn-{turn:04d}"


class Worker:
    def __init__(self, client: Client, agent_factory: Callable[[Dict[str, Any], List[Dict[str, Any]]], Agent] = create_agent,
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
            agent = self._agent_factory(snapshot, claim.get("tools", []))
            use_budget = getattr(agent, "use_budget", None)
            if use_budget:
                use_budget(self._reserve)
        except ApiError as error:
            return self._finish(run_id, "failed", f"runtime: initialization rejected ({error.reason_code})", 0)
        except PromptMismatch:
            return self._finish(run_id, "failed", "runtime: prompt file does not match the run snapshot", 0)
        except SessionEnded:
            return RunResult(run_id, "stopped", 0, "session ended")
        except Unreachable:
            return self._finish(run_id, "failed", "runtime: API unreachable", 0)
        except Exception as error:  # the class name only: messages may hold secrets or request content
            return self._finish(run_id, "failed", f"runtime: {type(error).__name__}", 0)

        deadline = self._clock() + int(snapshot["maxWallTimeSeconds"])
        # The server enforces maxActions (ADR-0009); this is only a safety net one turn beyond it.
        max_turns = int(snapshot["maxActions"]) + 1
        last_tool: Optional[str] = None
        last_response: Optional[Dict[str, Any]] = None
        last_results: List[Tuple[str, Dict[str, Any]]] = []
        idle_turns = 0
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
                try:
                    with self._renewed_session(deadline + RENEW_GRACE_SECONDS):
                        decision = agent.next_action(Observation(snapshot["objective"], claim.get("tools", []), turn,
                                                                 last_tool, last_response, last_results))
                except Exception as error:
                    self._report_usage(turn, getattr(error, "usage", None))
                    if isinstance(error, ModelApiError):
                        return self._finish(run_id, "failed", f"runtime: model API error ({error})", turn)
                    if isinstance(error, WallTimeReached):
                        return self._finish(run_id, "budget_exceeded", "runtime: wall-time budget reached", turn)
                    if isinstance(error, ReservationDenied):
                        if error.reason_code in TERMINAL_REASONS:
                            return RunResult(run_id, "stopped", turn, error.reason_code)
                        return self._finish(run_id, "failed", f"runtime: reservation rejected ({error.reason_code})", turn)
                    raise
                calls = decision.tool_calls
                invocation = self._client.model_invocation(
                    turn, decision.input_tokens, decision.output_tokens, decision.latency_ms, decision.output_text,
                    cacheReadInputTokens=decision.cache_read_tokens, cacheCreationInputTokens=decision.cache_creation_tokens,
                    model=decision.model, stopReason=decision.stop_reason, refusalCategory=decision.refusal_category,
                    toolUseCount=len(calls))
                if invocation.get("reason_code") in TERMINAL_REASONS:
                    return RunResult(run_id, "stopped", turn, invocation["reason_code"])
                if invocation.get("status") != "ok":
                    # Every turn must be recorded; never continue past a rejected record.
                    return self._finish(run_id, "failed", f"runtime: invocation record rejected ({invocation.get('reason_code')})", turn)
                if decision.terminal == "refusal":
                    return self._finish(run_id, "failed", f"model refusal ({decision.refusal_category or 'uncategorized'})", turn)
                if decision.terminal == "max_tokens":
                    return self._finish(run_id, "failed", "model output limit reached twice", turn)

                if decision.terminal == "unrepresentable" and not calls:
                    return self._finish(run_id, "failed", UNREPRESENTABLE_REASON, turn)
                if not calls:
                    idle_turns += 1
                    if idle_turns >= MAX_IDLE_TURNS:
                        return self._finish(run_id, "completed", "agent stopped calling tools", turn)
                    last_results = []
                    continue
                idle_turns = 0

                last_results = []
                for index, (tool, arguments) in enumerate(calls, start=1):
                    call_id = tool_call_id(turn) if len(calls) == 1 else f"{tool_call_id(turn)}-{index}"
                    response = self._client.tool_call(call_id, tool, arguments)
                    last_results.append((tool, response))
                    reason = response.get("reason_code")
                    if reason in TERMINAL_REASONS:
                        return RunResult(run_id, "stopped", turn, reason)
                    # After the virtual duration ends, agents read their balance and call finish.
                    if tool == "finish" and response.get("status") == "ok":
                        return self._finish(run_id, "completed", "agent finished", turn)
                if decision.terminal == "unrepresentable":
                    return self._finish(run_id, "failed", UNREPRESENTABLE_REASON, turn)
                last_tool, last_response = last_results[-1]
        except SessionEnded:
            return RunResult(run_id, "stopped", turn, "session ended")
        except Unreachable:
            return self._finish(run_id, "failed", "runtime: API unreachable", turn)
        except ApiError as error:
            return self._finish(run_id, "failed", f"runtime: request rejected (HTTP {error.status})", turn)
        except Exception as error:
            return self._finish(run_id, "failed", f"runtime: {type(error).__name__}", turn)

    def _reserve(self, turn: int, attempt: int, input_tokens: int, max_output_tokens: int) -> int:
        response = self._client.reserve_model_call(turn, attempt, input_tokens, max_output_tokens)
        if response.get("status") != "ok":
            raise ReservationDenied(response.get("reason_code"))
        return int(response["data"]["max_output_tokens"])

    def _report_usage(self, turn: int, usage: Optional[Dict[str, int]]) -> None:
        """Usage of a turn that failed. The server accepts it even if the run has already stopped."""
        if not usage:
            return
        try:
            self._client.model_invocation(
                turn, usage["input"], usage["output"], usage["latency"], "",
                cacheReadInputTokens=usage["cache_read"], cacheCreationInputTokens=usage["cache_creation"],
                stopReason="error", settledAttempts=usage["settled"])
        except Exception as error:
            log.warning("usage for turn %d not recorded, its reservation stays: %s", turn, type(error).__name__)

    @contextlib.contextmanager
    def _renewed_session(self, until: float) -> Iterator[None]:
        done = threading.Event()

        def renew() -> None:
            while not done.wait(SESSION_RENEW_SECONDS) and self._clock() < until:
                try:
                    self._client.heartbeat()
                except SessionEnded:
                    return
                except Exception as error:
                    log.warning("session renewal failed: %s", type(error).__name__)

        thread = threading.Thread(target=renew, daemon=True)
        thread.start()
        try:
            yield
        finally:
            done.set()
            thread.join(1.0)

    def _finish(self, run_id: str, outcome: str, reason: str, turns: int) -> RunResult:
        try:
            self._client.finish(outcome, reason)
        except Exception as error:
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
        try:
            result = worker.run_once()
        except SessionEnded:
            log.error("the API rejected the worker credential.")
            return 2
        except Exception as error:
            log.warning("claim failed: %s", type(error).__name__)
            if args.once:
                return 1
            time.sleep(args.poll_seconds)
            continue
        if args.once:
            return 0 if result is None or result.outcome != "failed" else 1
        if result is None:
            time.sleep(args.poll_seconds)


if __name__ == "__main__":
    sys.exit(main())
