"""Manual smoke run with real Claude (ADR-0012). Spends real money; run only on purpose.

Creates one short S01 run (3 virtual days, at most US$0.50), runs it with the real Claude adapter, and checks
the trace with the same rules as the end-to-end tests. Other queued mock runs it meets are run as mocks;
other queued Claude runs are refused (marked failed) so this script never spends money on them.

Usage (from services/agent-runtime, API running, SDK installed with pip install -r requirements.txt):
  FINMIND_API_URL=http://127.0.0.1:5080 FINMIND_WORKER_TOKEN=... python3 scripts/smoke_claude.py [model]
Credentials: the SDK reads ANTHROPIC_API_KEY or an `ant auth login` profile from this shell only.
Never put the key in the .env file the API loads.
"""

import json
import os
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from finmind_runtime.agents import create_agent  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402

API = os.environ.get("FINMIND_API_URL", "http://127.0.0.1:5080")
TOKEN = os.environ.get("FINMIND_WORKER_TOKEN", "")
MODEL = sys.argv[1] if len(sys.argv) > 1 else "claude-opus-5-5"
MAX_COST_MICRO_USD = 500_000


def http(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + path, data=data, method=method, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def main() -> int:
    if len(TOKEN) < 32:
        print("Set FINMIND_WORKER_TOKEN (at least 32 characters, same as the API).")
        return 2
    marker = f"Manage the provided virtual funds responsibly. [smoke {uuid.uuid4().hex[:8]}]"
    run_id = http("POST", "/internal/runs", {
        "objective": marker, "scenarioId": "S01", "seed": 1, "startingBalanceSen": 1_000_000,
        "transactionLimitSen": 400_000, "durationDays": 3, "maxActions": 30, "maxModelTokens": 300_000,
        "maxWallTimeSeconds": 900, "memoryMode": "none", "maxCostMicroUsd": MAX_COST_MICRO_USD,
        "describeUnavailableCapabilities": True,
        "agent": {"provider": "anthropic", "modelId": MODEL, "settings": {"effort": "medium"}}})["runId"]
    http("POST", f"/internal/runs/{run_id}/start")
    print(f"run {run_id} on {MODEL}, cost cap US${MAX_COST_MICRO_USD / 1_000_000:.2f}")

    def factory(snapshot, tools):
        if snapshot["objective"] == marker or snapshot["agent"]["provider"] == "mock":
            return create_agent(snapshot, tools)
        raise ValueError("smoke script runs only its own Claude run")

    worker = Worker(Client(API, TOKEN), agent_factory=factory)
    for _ in range(50):
        result = worker.run_once()
        if result is None or result.run_id == run_id:
            break

    run = http("GET", f"/internal/runs/{run_id}")
    events = http("GET", f"/internal/runs/{run_id}/events?limit=500")["events"]
    invocations = [e for e in events if e["eventType"] == "MODEL_INVOCATION_COMPLETED"]
    tool_results = [e for e in events if e["eventType"] == "TOOL_RETURNED"]
    cost = sum(e["payload"]["costMicroUsd"] for e in invocations)
    print(f"status {run['status']} ({run['statusReason']}), turns {len(invocations)}, tool calls {len(tool_results)}")
    print(f"cost US${cost / 1_000_000:.4f}, cache reads {sum(e['payload']['cacheReadInputTokens'] for e in invocations)} tokens")
    print("tool outcomes:", sorted({(e['payload']['tool'], e['payload']['status']) for e in tool_results}))
    intact = http("GET", f"/internal/runs/{run_id}/events/verify")["intact"]
    print("event chain intact:", intact)
    proposals = [e for e in events if e["eventType"] == "TOOL_CALL_PROPOSED"]
    ok = intact and run["status"] in {"COMPLETED", "BUDGET_EXCEEDED", "FAILED"} and len(proposals) == len(tool_results)
    print("SMOKE", "PASSED" if ok else "FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
