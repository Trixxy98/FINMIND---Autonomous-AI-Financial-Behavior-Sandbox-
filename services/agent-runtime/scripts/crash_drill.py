"""Crash and concurrency drill (ADR-0017 step 3) with real processes and mock agents only. No provider is called.

It starts its own API on 127.0.0.1:5080 against a test database (the database name must end in "_test"), with
generated tokens and a 20-second worker session, and runs three drills:

  worker_killed   a worker is killed (SIGKILL) in the middle of a run; another worker carries on with the queue.
  api_restarted   the API is killed (SIGKILL) in the middle of a run and started again.
  two_workers     two workers share one queue of runs.

After each drill, every run it touched must have an intact event chain, reconciled wallets, balanced journals,
and at most one ledger commit per tool call.

Usage (from the repository root, API built in Release, port 5080 free):
  FINMIND_API_TEST_DATABASE='Host=...;Database=finmind_test;...' python3 services/agent-runtime/scripts/crash_drill.py
"""

import json
import os
import re
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "services" / "agent-runtime"
API_DLL = ROOT / "apps" / "api" / "bin" / "Release" / "net10.0" / "Finmind.Api.dll"
API = "http://127.0.0.1:5080"
SESSION_SECONDS = 20
TERMINAL = {"COMPLETED", "FAILED", "CANCELLED", "BUDGET_EXCEEDED"}

# A worker process that runs mock agents only, optionally slowed down per turn so it can be interrupted mid-run.
WORKER_CODE = """
import sys, time
from finmind_runtime.agents import create_agent
from finmind_runtime.client import Client
from finmind_runtime.worker import Worker
def mock_only(snapshot, tools):
    if snapshot["agent"]["provider"] != "mock":
        raise ValueError("the drill runs mock agents only")
    return create_agent(snapshot, tools)
delay = float(sys.argv[1])
worker = Worker(Client(sys.argv[2], sys.argv[3]), agent_factory=mock_only, on_turn=lambda run, turn: time.sleep(delay))
while True:
    try:
        if worker.run_once() is None:
            time.sleep(0.5)
    except Exception as error:  # the API may be gone for a moment; keep polling, as the real worker does
        print("worker:", type(error).__name__, flush=True)
        time.sleep(0.5)
"""


class Drill:
    def __init__(self, database, log_dir):
        self.database = database
        self.worker_token = secrets.token_hex(24)
        self.researcher_token = secrets.token_hex(24)
        self.log_dir = Path(log_dir)
        self.api = None
        self.workers = []
        self.started_workers = 0

    # --- processes -------------------------------------------------------------------------------------------
    def start_api(self):
        env = {"PATH": os.environ["PATH"], "HOME": os.environ.get("HOME", "/tmp"),
               "ConnectionStrings__Finmind": self.database, "FINMIND_WORKER_TOKEN": self.worker_token,
               "FINMIND_RESEARCHER_TOKEN": self.researcher_token, "FINMIND_SESSION_LIFETIME_SECONDS": str(SESSION_SECONDS)}
        log = open(self.log_dir / "drill-api.log", "ab")
        self.api = subprocess.Popen(["dotnet", str(API_DLL)], env=env, stdout=log, stderr=subprocess.STDOUT)
        for _ in range(60):
            try:
                with urllib.request.urlopen(API + "/health", timeout=2) as response:
                    if response.status == 200:
                        return
            except OSError:
                time.sleep(0.5)
        raise RuntimeError("the API did not start; see drill-api.log")

    def kill_api(self):
        self.api.send_signal(signal.SIGKILL)
        self.api.wait()

    def start_worker(self, delay=0.0):
        env = {"PATH": os.environ["PATH"], "PYTHONPATH": str(RUNTIME)}
        self.started_workers += 1
        log = open(self.log_dir / f"drill-worker-{self.started_workers}.log", "ab")
        process = subprocess.Popen([sys.executable, "-B", "-c", WORKER_CODE, str(delay), API, self.worker_token],
                                   env=env, cwd=RUNTIME, stdout=log, stderr=subprocess.STDOUT)
        self.workers.append(process)
        return process

    @staticmethod
    def kill(process):
        process.send_signal(signal.SIGKILL)
        process.wait()

    def stop_all(self):
        for process in self.workers:
            if process.poll() is None:
                self.kill(process)
        if self.api and self.api.poll() is None:
            self.api.terminate()
            self.api.wait(timeout=30)

    # --- researcher API --------------------------------------------------------------------------------------
    def call(self, method, path, payload=None):
        data = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(API + "/api/v1" + path, data=data, method=method, headers={
            "Content-Type": "application/json", "Authorization": "Bearer " + self.researcher_token})
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read() or b"null")

    def new_run(self, seed=42):
        run = self.call("POST", "/runs", {
            "objective": "Manage the provided virtual funds responsibly for 30 days.", "scenarioId": "S01", "seed": seed,
            "startingBalanceSen": 1_000_000, "transactionLimitSen": 400_000, "durationDays": 30, "maxActions": 200,
            "maxModelTokens": 0, "maxWallTimeSeconds": 600, "memoryMode": "none",
            "agent": {"provider": "mock", "modelId": "finmind-mock-v1"}})["runId"]
        self.call("POST", f"/runs/{run}/start")
        return run

    def pages(self, path, key):
        items, after = [], 0
        while True:
            page = self.call("GET", f"{path}?limit=500&afterSequence={after}")
            if not page[key]:
                return items
            items += page[key]
            after = page["nextAfterSequence"]

    def status(self, run):
        detail = self.call("GET", f"/runs/{run}")
        return detail["status"], detail.get("statusReason")

    def wait_for(self, condition, seconds, what):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if condition():
                return
            time.sleep(0.5)
        raise AssertionError(f"timed out waiting for {what}")

    def action_count(self, run):
        return len(self.call("GET", f"/runs/{run}/actions?limit=500")["actions"])

    def invariants(self, run):
        """Problems found in one run's record; an empty list means it is consistent."""
        problems = []
        if not self.call("GET", f"/runs/{run}/verify")["intact"]:
            problems.append("event chain broken")
        if not self.call("GET", f"/runs/{run}/wallets")["reconciled"]:
            problems.append("wallets do not reconcile")
        for journal in self.pages(f"/runs/{run}/ledger", "journals"):
            if sum(p["amountSen"] for p in journal["postings"]) != 0:
                problems.append(f"journal {journal['journalId']} does not balance")
        events = self.pages(f"/runs/{run}/events", "events")
        commits = Counter(e.get("traceId") for e in events if e["eventType"] == "LEDGER_COMMITTED" and e.get("traceId"))
        problems += [f"tool call {call} committed {n} times" for call, n in commits.items() if n > 1]
        return problems


def drill_worker_killed(d):
    run = d.new_run()
    worker = d.start_worker(delay=0.3)
    d.wait_for(lambda: d.action_count(run) >= 5, 60, "the run to get going")
    d.kill(worker)
    killed_at = d.action_count(run)
    assert d.status(run)[0] == "RUNNING", d.status(run)
    after = d.new_run(seed=43)
    d.start_worker()
    d.wait_for(lambda: d.status(run)[0] in TERMINAL and d.status(after)[0] in TERMINAL, SESSION_SECONDS * 4, "both runs to end")
    assert d.status(run) == ("FAILED", "Worker session expired."), d.status(run)
    assert d.status(after)[0] == "COMPLETED", d.status(after)
    assert d.action_count(run) == killed_at, "the killed run moved after its worker died"
    return {"killedRun": d.status(run), "actionsBeforeKill": killed_at, "nextRun": d.status(after),
            "problems": {r: d.invariants(r) for r in (run, after)}}


def drill_api_restarted(d):
    run = d.new_run(seed=44)
    d.start_worker(delay=0.3)
    d.wait_for(lambda: d.action_count(run) >= 5, 60, "the run to get going")
    assert d.status(run)[0] == "RUNNING", d.status(run)
    d.kill_api()
    d.start_api()
    at_restart = d.action_count(run)
    d.wait_for(lambda: d.status(run)[0] in TERMINAL, SESSION_SECONDS * 4 + 60, "the run to end")
    status = d.status(run)
    # Either the worker's retries carried the run through, or its session expired and the run failed safely.
    assert status[0] == "COMPLETED" or status == ("FAILED", "Worker session expired."), status
    assert d.action_count(run) > at_restart or status[0] == "FAILED", "the run did not continue after the restart"
    return {"run": status, "actionsAtRestart": at_restart, "actionsAtEnd": d.action_count(run), "problems": {run: d.invariants(run)}}


def drill_two_workers(d):
    runs = [d.new_run(seed=100 + i) for i in range(6)]
    d.start_worker()
    d.start_worker()
    d.wait_for(lambda: all(d.status(r)[0] in TERMINAL for r in runs), 120, "every run to end")
    claims = {}
    for run in runs:
        events = d.pages(f"/runs/{run}/events", "events")
        claims[run] = sum(e["eventType"] == "RUN_STATUS_CHANGED" and e["payload"].get("to") == "INITIALIZING" for e in events)
        assert not any(e["eventType"] == "RUN_RECLAIMED" for e in events), f"{run} was reclaimed"
    assert all(n == 1 for n in claims.values()), claims
    assert all(d.status(r)[0] == "COMPLETED" for r in runs), [d.status(r) for r in runs]
    return {"runs": len(runs), "claimsPerRun": sorted(set(claims.values())), "problems": {r: d.invariants(r) for r in runs}}


DRILLS = [drill_worker_killed, drill_api_restarted, drill_two_workers]


def main():
    database = os.environ.get("FINMIND_API_TEST_DATABASE", "")
    name = re.search(r"Database=([^;]+)", database, re.IGNORECASE)
    if not name or not name.group(1).endswith("_test"):
        print("Set FINMIND_API_TEST_DATABASE to a test database (its name must end in _test).")
        return 2
    if not API_DLL.is_file():
        print(f"Build the API first: dotnet build apps/api/Finmind.Api.csproj --configuration Release ({API_DLL} not found).")
        return 2
    with socket.socket() as probe:
        if probe.connect_ex(("127.0.0.1", 5080)) == 0:
            print("Port 5080 is in use. The drill starts its own API; stop the other one first.")
            return 2
    log_dir = Path(os.environ.get("FINMIND_DRILL_LOG_DIR") or tempfile.mkdtemp(prefix="finmind-drill-"))
    drill = Drill(database, log_dir)
    results, passed = {}, True
    try:
        drill.start_api()
        # Runs left queued by earlier tests are cancelled by the drill's own stop, so the queue holds only its runs.
        drill.call("POST", "/admin/stop", {"reason": "crash drill: clear the queue"})
        drill.call("POST", "/admin/resume", {"reason": "crash drill: start"})
        for run in DRILLS:
            try:
                result = run(drill)
                bad = {r: p for r, p in result.pop("problems").items() if p}
                results[run.__name__] = {"passed": not bad, **result, **({"problems": bad} if bad else {})}
                passed &= not bad
            except AssertionError as error:
                results[run.__name__] = {"passed": False, "error": str(error)}
                passed = False
            for worker in drill.workers:
                if worker.poll() is None:
                    drill.kill(worker)
            drill.workers.clear()
    finally:
        drill.stop_all()
    tokens = (drill.worker_token, drill.researcher_token)
    leaked = any(t in (log_dir / f).read_text(errors="replace") for f in os.listdir(log_dir) if f.startswith("drill-")
                 for t in tokens)
    print(json.dumps({"results": results, "tokensInLogs": leaked}, indent=2))
    print(f"logs: {log_dir}")
    print("DRILL", "PASSED" if passed and not leaked else "FAILED")
    return 0 if passed and not leaked else 1


if __name__ == "__main__":
    sys.exit(main())
