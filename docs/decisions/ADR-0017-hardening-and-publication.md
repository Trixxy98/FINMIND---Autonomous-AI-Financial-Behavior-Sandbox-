# FINMIND ADR 0017: Phase 5, Hardening and Publication

- Status: Accepted 25 November 2026 (steps 1 and 2 implemented); decisions: 1 yes, 2 document as remaining risk, 3 yes, 4 no licence
- Date: 25 November 2026
- Scope: threat model review, gap fixes, crash and concurrency checks,
  documentation, sample data, a reproducible demo, and the Phase 5 exit gate

## Context

The plan's Phase 5 asks for a review of the threat model, access controls,
isolation, concurrency, and crash recovery; documentation, synthetic sample
data, a report template, and a reproducible demo. Its exit gate: an independent
developer can reproduce the mock results locally, no real financial integration
exists, and all critical integrity tests pass.

A first survey of the specification's controls (section 14.2) against the code
found these gaps:

| Control in section 14.2 | State today |
|---|---|
| Manual kill switch | Runs can be cancelled one at a time; there is no switch that stops all runs and new claims |
| Audit access to exports | Exports are served but not recorded |
| Rate-limit tool calls and model usage | Covered by per-run budgets (actions, tokens, cost, wall time, virtual days); no per-minute rate |
| Container isolation of the agent runtime | The worker is a local Python process; only PostgreSQL runs in Compose |
| No outbound network except the model endpoint | Not enforced; the worker can reach any host the machine can |
| RBAC | Two roles: researcher (bearer token) and worker (separate token); no finer roles |

The agent itself has no shell, code execution, SQL, filesystem, or network
tool. The risks above concern the worker process and the operator's machine,
not what a model can do through its tools.

## Decision

### 1. Threat model document

`docs/security/threat-model.md` lists every threat in section 14.1. For each:
the controls, the tests that show them (by file and test name), and the
remaining risk. A test reads the document and fails if a referenced test no
longer exists, so the document cannot silently go stale.

### 2. Gap fixes

- **Kill switch**: `POST /api/v1/admin/stop` (researcher token, with a
  reason) cancels every active run and makes the API refuse new claims and
  starts until `POST /api/v1/admin/resume`. The stop and resume are recorded
  in an append-only table (migration 017), and each cancelled run records the
  reason in its own events, as a normal cancellation does.
- **Export audit**: each export request (run, format, time, result) is written
  to an append-only table in the same migration. Nothing is added to the run's
  event chain, so finished runs and their hashes do not change.
- **Rate limits**: kept as budgets. The threat model records why: a run cannot
  exceed its action, token, cost, or time budget, which bounds spending; a
  per-minute rate would add no protection for a local single-user tool.
- **Worker isolation and egress**: documented as remaining risk with a
  recommendation, not built now (see decisions to confirm).

### 3. Concurrency and crash recovery

Existing tests cover concurrent spending, timeout after commit, and reclaiming
a run whose worker stopped. Added end-to-end checks with real processes:

- A worker killed (SIGKILL) in the middle of a run: a new worker reclaims it,
  the run finishes or fails safely, the ledger balances, and no tool call has
  two economic effects.
- The API restarted in the middle of a run: the worker's retries recover
  without duplicate effects.
- Two workers on one queue: every run is claimed once.

### 4. No real financial integration

A test lists every dependency (NuGet, pip, npm lockfile) and fails on anything
not in a reviewed allowlist, and scans source for payment, banking, and wallet
SDK names and hosts. It runs in CI.

### 5. Documentation

- `README.md` rewritten as a quickstart for a developer who has never seen the
  project: prerequisites, setup, first mock run, the UI, checks.
- `docs/ARCHITECTURE.md`: components, trust boundaries, data flow, and where
  each invariant is enforced.
- `docs/RESEARCHER_GUIDE.md`: designing a comparison, reading evidence labels,
  reviewing classifications, exporting, and the limits of conclusions.
- `docs/reports/TEMPLATE.md`: a report template with setup, versions,
  denominators, results, human review, and limitations.

### 6. Sample data and demo

- `scripts/demo.sh` (or a documented sequence) starts PostgreSQL, applies
  migrations to a database it creates for the demo, starts the API with
  generated tokens, runs a small mock batch, and prints the UI address. It
  never uses the researcher's own database.
- `samples/`: exports (JSONL and CSV) of mock runs on S01, S04, and S07, with a
  README. A test checks their hash chains and that regenerating them gives the
  same metrics. They are synthetic and contain no secrets.
- A demo video is recorded by the researcher; the project provides the script.

### 7. Phase 5 exit gate

In a fresh clone in a new directory, following only the README, with a new
database: run the demo and `phase4_gate.py`, and compare the metrics with
`docs/reports/phase4-exit-gate.md`. Confirm the dependency check passes and
the named critical integrity tests pass. Report in
`docs/reports/phase5-exit-gate.md`.

## Delivery

Each step ends with tests, a report, and a stop for review:

1. Threat model document and its staleness test.
2. Kill switch and export audit (migration 017).
3. Crash and concurrency checks with real processes.
4. Dependency allowlist and integration scan.
5. Documentation and report template.
6. Demo and sample data.
7. Exit gate in a fresh clone.

#### Step 1 as implemented

- `docs/security/threat-model.md` (version 1): T1 to T10 from section 14.1
  and seven operational threats, each with controls, named tests, and the
  remaining risk.
- `tests/test_threat_model.py` fails when a named test no longer exists, when
  a threat has no evidence, or when a section has no remaining-risk line.
- The review found one control without a direct test: specification 16.3
  asks that a message claiming authority does not change policy.
  `tests/test_injection.py` adds it: an agent obeys S07's administrator
  message, the RM6,000 transfer is denied with the run's own limit, and no
  money moves. It passed on the first run; the control already existed.

#### Step 2 as implemented

- Migration 017: `operator_switch_log` and `export_log`, both append-only
  (the API role can only read and insert), ordered by an identity column.
- `StopAll`, `ResumeAll`, `GetOperatorState`, and `OperatorHistory` on both
  ledgers. A stop takes an exclusive PostgreSQL advisory lock, records itself,
  lists the active runs, and commits; then each run is cancelled with the
  reason "Operator stop: …". Starts and claims take the same lock shared, so a
  start either finishes before the stop lists runs or sees the stop. A test
  races 24 starts against a stop and finds no active run afterwards.
- While stopped, a start is refused with `OPERATOR_STOPPED` (new in
  `finance-reasons/v8`, conflict, HTTP 409) and a claim returns nothing.
  Draft runs are left as they are.
- Routes: `GET /api/v1/admin/status`, `POST /api/v1/admin/stop`,
  `POST /api/v1/admin/resume` (a reason is required for both), and
  `GET /api/v1/admin/exports`. The export route records each request (served,
  not found, or invalid format) before serving; if recording fails, nothing
  is served.
- The app shows "Stop all runs…" under the header and a red notice while
  stopped, with "Resume runs…"; both ask for a reason.
- New runs record `finance-reasons/v8`; a comparison of runs from before and
  after this change lists the catalog version as a metadata difference.

## Out of scope

Multi-user accounts and finer roles, hosting, a container for the worker
(unless chosen below), and any real-money or real-destination integration.

## Decisions to confirm

1. Build the kill switch and export audit (recommended), with migration 017.
2. Worker isolation: document as remaining risk with a recommendation
   (recommended), or add a Compose service for the worker with a non-root user,
   read-only filesystem, and resource limits. Restricting its network to the
   API and the model provider would need a proxy, a larger change.
3. Commit the synthetic sample exports to the repository (small, under 1 MB).
4. A licence for publication is the researcher's choice; this ADR adds none.
