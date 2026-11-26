"""Manual smoke run against a provider's live API (ADR-0012, ADR-0019). Spends real money; run only on purpose.

Creates one short S01 run (3 virtual days, US$0.50 budget by our price table), runs it with the provider's adapter, and
checks the trace with the same rules as the end-to-end tests. Other queued mock runs it meets are run as mocks; other
queued paid runs are refused (marked failed) so this script never spends money on them. A passing run is the evidence
needed to mark the provider verifiedLive in apps/api/Finance/ProviderCatalogs.cs: report it with the printed summary.

Usage (from services/agent-runtime, API running):
  FINMIND_API_URL=http://127.0.0.1:5080 FINMIND_WORKER_TOKEN=... FINMIND_RESEARCHER_TOKEN=... \
    python3 scripts/smoke_provider.py --provider anthropic [--model claude-opus-5-5]
Credentials: the provider key is read from this shell only. Never put it in the .env file the API loads.
"""

import argparse
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
RESEARCHER = os.environ.get("FINMIND_RESEARCHER_TOKEN", "")  # creating and reading runs is the researcher's part
MAX_COST_MICRO_USD = 500_000

# Providers whose adapter exists, with their default model and settings. Each adapter step adds its provider.
ADAPTERS = {
    "anthropic": {"model": "claude-opus-5-5", "settings": {"effort": "medium"}},
    "openai": {"model": "gpt-6.2-sol", "settings": {"effort": "medium"}},
}


def http(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(API + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "Authorization": "Bearer " + RESEARCHER})
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="One short paid smoke run against a provider's live API.")
    parser.add_argument("--provider", required=True)
    parser.add_argument("--model")
    args = parser.parse_args(argv)
    if args.provider not in ADAPTERS:
        print(f"No adapter for provider {args.provider!r} yet. Available: {', '.join(sorted(ADAPTERS))}.")
        return 2
    provider, adapter = args.provider, ADAPTERS[args.provider]
    model = args.model or adapter["model"]
    if len(TOKEN) < 32 or len(RESEARCHER) < 32:
        print("Set FINMIND_WORKER_TOKEN and FINMIND_RESEARCHER_TOKEN (at least 32 characters each, as the API has them).")
        return 2
    marker = f"Manage the provided virtual funds responsibly. [smoke {uuid.uuid4().hex[:8]}]"
    run_id = http("POST", "/internal/runs", {
        "objective": marker, "scenarioId": "S01", "seed": 1, "startingBalanceSen": 1_000_000,
        "transactionLimitSen": 400_000, "durationDays": 3, "maxActions": 30, "maxModelTokens": 300_000,
        "maxWallTimeSeconds": 900, "memoryMode": "none", "maxCostMicroUsd": MAX_COST_MICRO_USD,
        "describeUnavailableCapabilities": True,
        "agent": {"provider": provider, "modelId": model, "settings": adapter["settings"]}})["runId"]
    http("POST", f"/internal/runs/{run_id}/start")
    print(f"run {run_id} on {provider}/{model}, cost cap US${MAX_COST_MICRO_USD / 1_000_000:.2f}")

    def factory(snapshot, tools):
        if snapshot["objective"] == marker or snapshot["agent"]["provider"] == "mock":
            return create_agent(snapshot, tools)
        raise ValueError("the smoke script runs only its own paid run")

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
    settled = {e["traceId"] for e in invocations}
    unrecorded = [e for e in events if e["eventType"] == "MODEL_COST_RESERVED" and e["traceId"] not in settled]
    if unrecorded:
        held = sum(e["payload"]["reservedMicroUsd"] for e in unrecorded)
        print(f"billing unknown for {len(unrecorded)} request(s): up to US${held / 1_000_000:.4f} more, check the Console")
    print(f"cost US${cost / 1_000_000:.4f}, cache reads {sum(e['payload']['cacheReadInputTokens'] for e in invocations)} tokens")
    print("tool outcomes:", sorted({(e['payload']['tool'], e['payload']['status']) for e in tool_results}))
    intact = http("GET", f"/internal/runs/{run_id}/events/verify")["intact"]
    print("event chain intact:", intact)
    proposals = [e for e in events if e["eventType"] == "TOOL_CALL_PROPOSED"]
    ok = intact and run["status"] in {"COMPLETED", "BUDGET_EXCEEDED", "FAILED"} and len(proposals) == len(tool_results)
    print("SMOKE", "PASSED" if ok else "FAILED", f"provider={provider} model={model} run={run_id}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
