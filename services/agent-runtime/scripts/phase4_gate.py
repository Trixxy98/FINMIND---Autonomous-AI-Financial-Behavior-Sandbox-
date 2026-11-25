"""Phase 4 exit gate (ADR-0015 step 7), reproducible with mock agents only. No provider is ever called.

Two batches of three replications on scenario S01 that differ only in the objective (specification section 20, H1):
the S01 objective, and the impossible target that S03 packages. The pair is run twice to check reproducibility.

Usage (from services/agent-runtime, API running):
  FINMIND_API_URL=http://127.0.0.1:5080 FINMIND_WORKER_TOKEN=... FINMIND_RESEARCHER_TOKEN=... python3 scripts/phase4_gate.py

It refuses to start while any other run is queued, so it never claims someone else's run.
"""

import hashlib
import json
import os
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from finmind_runtime.agents import create_agent  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402

TERMINAL = {"COMPLETED", "FAILED", "CANCELLED", "BUDGET_EXCEEDED"}
REPLICATIONS = 3


class Gate:
    def __init__(self, api, worker_token, researcher_token):
        self.api, self.worker_token, self.researcher_token = api.rstrip("/"), worker_token, researcher_token

    def call(self, method, path, payload=None, raw=False):
        data = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(self.api + "/api/v1" + path, data=data, method=method,
                                         headers={"Content-Type": "application/json",
                                                  "Authorization": "Bearer " + self.researcher_token})
        with urllib.request.urlopen(request, timeout=120) as response:
            body = response.read()
            return body if raw else json.loads(body or b"null")

    def config(self, objective):
        scenario = next(s for s in self.call("GET", "/scenarios")["scenarios"] if s["id"] == "S01" and s["latest"])
        return {"objective": objective, "scenarioId": scenario["id"], "seed": 42, "startingBalanceSen": 1_000_000,
                "transactionLimitSen": 400_000, "durationDays": 30, "maxActions": 200, "maxModelTokens": 0,
                "maxWallTimeSeconds": 600, "memoryMode": "none",
                "agent": {"provider": "mock", "modelId": "finmind-mock-v1"}}

    def objectives(self):
        scenarios = {s["id"]: s for s in self.call("GET", "/scenarios")["scenarios"] if s["latest"]}
        return scenarios["S01"]["recommendedObjective"], scenarios["S03"]["recommendedObjective"]

    def queue_is_clear(self):
        return not self.call("GET", "/runs?status=QUEUED&limit=500")["runs"]

    def run_queue(self):
        """Runs what this gate queued. Anything that is not a mock run is refused, never sent to a provider."""
        def mock_only(snapshot, tools):
            if snapshot["agent"]["provider"] != "mock":
                raise ValueError("the gate runs mock agents only")
            return create_agent(snapshot, tools)
        worker = Worker(Client(self.api, self.worker_token), agent_factory=mock_only)
        while worker.run_once() is not None:
            pass

    def pair(self, label):
        realistic, impossible = self.objectives()
        batches = [self.call("POST", "/batches", {"label": f"{label} realistic objective", "config": self.config(realistic),
                                                  "count": REPLICATIONS, "start": True}),
                   self.call("POST", "/batches", {"label": f"{label} impossible objective", "config": self.config(impossible),
                                                  "count": REPLICATIONS, "start": True})]
        self.run_queue()
        comparison = self.call("GET", "/compare?" + "&".join(f"batch={b['batchId']}" for b in batches))
        return batches, comparison

    def verify_export(self, run_id):
        """The chain checked with hashlib alone, as an outside verifier would."""
        records = [json.loads(line) for line in self.call("GET", f"/runs/{run_id}/export?format=jsonl", raw=True).splitlines()]
        previous = None
        for event in (r for r in records if r["type"] == "event"):
            if hashlib.sha256(event["hashInput"].encode()).hexdigest() != event["eventHash"] or event["prevHash"] != previous:
                return False
            previous = event["eventHash"]
        return records[-1]["chainIntact"] is True


def stable(comparison):
    """Per-group metric values, without run IDs, for comparing two executions."""
    return {m["name"]: [sorted(json.dumps(v["value"]) for v in g["values"]) for g in m["groups"]] for m in comparison["metrics"]}


def run(gate):
    if not gate.queue_is_clear():
        raise SystemExit("Other runs are queued. The gate only runs its own runs; start it with an empty queue.")
    first_batches, first = gate.pair("Gate A")
    _, second = gate.pair("Gate B")
    run_ids = [v["runId"] for m in first["metrics"][:1] for g in m["groups"] for v in g["values"]]
    return {
        "first": first, "second": second, "batches": first_batches,
        "reproducible": stable(first) == stable(second),
        "exportsVerified": all(gate.verify_export(run_id) for run_id in run_ids),
    }


def report(result):
    first = result["first"]
    lines = ["| Metric | " + " | ".join(g["label"] for g in first["groups"]) + " |", "|---|---|---|"]
    for metric in first["metrics"]:
        cells = [f"{g['mean']} (n = {g['n']}{', ' + str(g['notApplicable']) + ' not applicable' if g.get('notApplicable') else ''})"
                 for g in metric["groups"]]
        lines.append(f"| {metric['name']} | " + " | ".join(cells) + " |")
    return "\n".join([
        f"comparison: {first['comparisonVersion']}, metrics: {first['metricsVersion']}",
        f"between groups: {first['configuration']['betweenGroups']}",
        f"within groups: {first['configuration']['withinGroups']}",
        f"metadata differences: {first['configuration']['metadata']}",
        f"included: {[g['n'] for g in first['groups']]}, excluded: {[len(g['excluded']) for g in first['groups']]}",
        f"reproducible across two executions: {result['reproducible']}",
        f"exports verified with hashlib: {result['exportsVerified']}",
        "", *lines])


def main():
    api = os.environ.get("FINMIND_API_URL", "http://127.0.0.1:5080")
    worker, researcher = os.environ.get("FINMIND_WORKER_TOKEN", ""), os.environ.get("FINMIND_RESEARCHER_TOKEN", "")
    if len(worker) < 32 or len(researcher) < 32:
        print("Set FINMIND_WORKER_TOKEN and FINMIND_RESEARCHER_TOKEN (at least 32 characters each).")
        return 2
    result = run(Gate(api, worker, researcher))
    print(report(result))
    passed = (result["reproducible"] and result["exportsVerified"]
              and result["first"]["configuration"]["betweenGroups"] == ["objective"]
              and all(g["n"] == REPLICATIONS for g in result["first"]["groups"]))
    print("GATE", "PASSED" if passed else "FAILED")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
