# FINMIND ADR 0008: Run Lifecycle and Immutable Configuration Snapshot

- Status: Implemented for Phase 2 local API (runtime transitions wait for the ADR-0009 worker interface)
- Date: 21 November 2026
- Scope: Phase 2 — managed runs with a server-validated configuration snapshot and server-enforced lifecycle

## Context

Today a run exists as soon as `OpenRun` is called with a caller-chosen
opening balance and transaction cap. There is no lifecycle, no record of
the experiment configuration, and nothing stops finance operations after
a run should have ended.

The specification requires (sections 5, 9.3, 12; technical details
section 5):

- A run snapshots its full configuration; later template changes must not
  change an existing run.
- Lifecycle states `DRAFT → QUEUED → INITIALIZING → RUNNING →
  COMPLETED | FAILED | CANCELLED | BUDGET_EXCEEDED`, validated on the
  server. Terminal states are immutable.
- A cancelled run must not accept new tool proposals.
- Reproducibility metadata: model and provider, prompt, tool, policy, and
  scenario versions, seed, budgets, and sampling parameters.

## Decision summary

- Introduce **managed runs**: created from a validated configuration
  snapshot, with a status column and a database-enforced transition table.
- The snapshot is stored once, as JSONB with a SHA-256 hash, and can never
  change.
- Opening funds and the transaction cap come **only** from the snapshot.
  Callers cannot choose them for a managed run.
- Money-moving operations on a managed run are allowed only while it is
  `RUNNING` (opening funding only during `INITIALIZING`).
- Existing runs created through the Phase 1 `OpenRun` path become
  **unmanaged legacy runs** (`status` NULL). They keep Phase 1 behavior so
  existing tests and data remain valid. They are development scaffolding
  and are excluded from research results.
- Experiment templates (`/experiments`) are deferred. A run is created with
  its full configuration directly; `template_id` is reserved.

## Configuration snapshot (schema `run-config/v1`)

Supplied by the researcher:

| Field | Type | Rules |
|---|---|---|
| `objective` | string | 1–2,000 characters; untrusted text shown to the agent, never instructions to the platform |
| `scenarioId` | string | Must exist in the server's scenario catalog (ADR-0010) |
| `seed` | integer | 0 to 2^31−1 |
| `startingBalanceSen` | integer | 1 to 10,000,000,000 (RM100 million) |
| `transactionLimitSen` | integer | 1 to `startingBalanceSen` |
| `durationDays` | integer | 1 to 365 (virtual days) |
| `maxActions` | integer | 1 to 1,000 tool calls |
| `maxModelTokens` | integer | 0 to 5,000,000 (0 allowed only for the mock agent) |
| `maxWallTimeSeconds` | integer | 1 to 86,400 |
| `memoryMode` | string | `none` or `bounded_summary` |
| `agent.provider` | string | Must be a configured provider. Phase 2 configures only `mock` |
| `agent.modelId` | string | Must be a configured model for that provider |
| `agent.sampling` | object | Provider-specific, validated against the provider's allowed keys; empty for `mock` |

Added by the server at creation (not accepted from the caller):

| Field | Source |
|---|---|
| `configSchemaVersion` | `run-config/v1` |
| `scenarioVersion` | Scenario catalog |
| `toolsetVersion` | Tool schema version (ADR-0009) |
| `policyVersion` | Finance policy version, starting `finance-policy/v1` |
| `reasonCodeCatalog` | `finance-reasons/v2` |
| `promptVersion` | Runtime prompt template version (ADR-0011); `none` for `mock` |
| `codeRevision` | `FINMIND_CODE_REVISION` environment variable, or `unknown` |

Unknown fields are rejected, not ignored. Validation failures return
`malformed` with a new reason code `INVALID_RUN_CONFIG` and a list of
field names (no echo of values).

The snapshot contains no credentials. Provider credentials are resolved
by the runtime from server configuration at call time.

`config_hash = sha256(convert_to('finmind-run-config-v1' || E'\n' || config_snapshot::text, 'UTF8'))`,
computed by a database trigger, using the same canonical JSONB text
approach as ADR-0007.

## Lifecycle

| From | To | Triggered by | Notes |
|---|---|---|---|
| (none) | `DRAFT` | Researcher creates run | Snapshot stored; run row created with the snapshot's transaction cap |
| `DRAFT` | `QUEUED` | Researcher starts run | |
| `QUEUED` | `INITIALIZING` | Runtime claims run | Worker identity defined in ADR-0009 |
| `INITIALIZING` | `RUNNING` | Runtime, after opening funding commits | Opening funding uses snapshot values |
| `RUNNING` | `COMPLETED` | Runtime | Agent finished or virtual duration ended |
| `RUNNING` | `BUDGET_EXCEEDED` | Runtime or finance boundary | Action, token, or wall-time budget reached |
| `INITIALIZING`, `RUNNING` | `FAILED` | Runtime | Unrecoverable error; reason recorded |
| `DRAFT`, `QUEUED`, `INITIALIZING`, `RUNNING` | `CANCELLED` | Researcher | Cooperative; see below |

Terminal states: `COMPLETED`, `FAILED`, `CANCELLED`, `BUDGET_EXCEEDED`.
Any other transition is rejected with a new reason code
`INVALID_RUN_TRANSITION` (outcome `conflict`).

Every transition records `status_reason` (short text, required for
`FAILED`, `CANCELLED`, `BUDGET_EXCEEDED`) and wall-clock timestamps
`queued_at`, `started_at` (entered `RUNNING`), and `finished_at`
(entered a terminal state).

### Cancellation

Cancellation is a status change, effective immediately at the finance
boundary: once a run is not `RUNNING`, every money-moving operation is
rejected with a new reason code `RUN_NOT_ACTIVE` (outcome `denied`) and
recorded as `POLICY_DENIED`. The runtime is expected to stop within a
bounded time (ADR-0011), but correctness does not depend on it stopping.

### Finance operations by status (managed runs)

| Operation | Allowed status |
|---|---|
| Opening funding | `INITIALIZING` only, through the lifecycle path |
| Transfer, savings move, obligation payment | `RUNNING` |
| Create account, create obligation | `INITIALIZING`, `RUNNING` (scenario setup and events) |
| Reversal | `RUNNING` (terminal runs are immutable) |
| Reads and event reads | Any status |

The direct `POST /internal/runs/{runId}/funding` endpoint rejects managed
runs with `RUN_NOT_ACTIVE`. It remains only for unmanaged legacy runs and
is removed once the runtime and UI create runs through the lifecycle.

## Schema (migration 008)

On `experiment_runs`, add:

- `status TEXT NULL` (NULL = unmanaged legacy run), CHECK in the eight states
- `status_reason TEXT NULL`
- `config_snapshot JSONB NULL`, `config_hash BYTEA NULL`
- `template_id UUID NULL` (reserved)
- `queued_at`, `started_at`, `finished_at TIMESTAMPTZ NULL`
- CHECK: `status` and `config_snapshot` are both NULL or both set

Triggers:

- `BEFORE INSERT`: computes `config_hash`; a managed run must be inserted
  as `DRAFT`.
- `BEFORE UPDATE`: rejects any change to `config_snapshot`, `config_hash`,
  or `template_id`; rejects status changes not in the transition table;
  rejects changing a legacy run to managed or back; sets the timestamps.
  Terminal rows reject all further status changes.

`finmind_api` gains `UPDATE (status, status_reason, queued_at, started_at,
finished_at)` on `experiment_runs`. It does not gain UPDATE on the snapshot
columns.

## Events

| Event | When | Payload |
|---|---|---|
| `RUN_CREATED` (v2) | Managed run created | `configHash`, `configSchemaVersion`, `scenarioId`, `seed`, `transactionLimitSen`, `startingBalanceSen` |
| `RUN_STATUS_CHANGED` | Every lifecycle transition | `from`, `to`, `reason` |

The full snapshot is not copied into events; the event carries its hash,
and the snapshot is read from the run. `RUN_CREATED` v1 (Phase 1
`OpenRun`) is unchanged for legacy runs. `RUN_STATUS_CHANGED` is written in
the same transaction as the status change. Rejected transitions are
recorded as `POLICY_DENIED`.

Spec event names map as: `RUN_STARTED` = `RUN_STATUS_CHANGED` to
`RUNNING`; `RUN_COMPLETED` / `RUN_FAILED` = to `COMPLETED` / `FAILED`.
One event type keeps the taxonomy small and every transition uniform.

## New reason codes (`finance-reasons/v3`, additive)

| Code | Outcome |
|---|---|
| `INVALID_RUN_CONFIG` | malformed |
| `INVALID_RUN_TRANSITION` | conflict |
| `RUN_NOT_ACTIVE` | denied |

## API (internal, localhost, unauthenticated as in Phase 1)

| Method | Path | Purpose |
|---|---|---|
| POST | `/internal/runs` | Create a managed run (`DRAFT`); server assigns the run ID |
| GET | `/internal/runs/{runId}` | Status, timestamps, snapshot, `configHash` |
| POST | `/internal/runs/{runId}/start` | `DRAFT → QUEUED` |
| POST | `/internal/runs/{runId}/cancel` | Body `{ "reason": "..." }` |

Runtime transitions (`QUEUED → INITIALIZING → RUNNING → terminal`) are
exposed only through the worker interface defined in ADR-0009, not as
researcher endpoints.

## Implementation notes

- Migration: `infra/db/migrations/008_run_lifecycle.sql` (re-runnable).
- Code: `apps/api/Finance/RunLifecycle.cs` (statuses, transition table,
  configuration validation, catalogs); lifecycle methods on
  `IFinanceLedger`: `CreateManagedRun`, `GetRun`, `TransitionRun`,
  `ActivateRun`. Both ledgers implement them; tests run against both.
- `ActivateRun` is the only way into `RUNNING`. It funds the run from the
  snapshot (idempotency key `run-opening`) and changes status in one
  transaction. `TransitionRun` rejects `RUNNING` as a target.
- The status check runs before idempotency handling. A retry of an earlier
  money operation after the run stops returns `RUN_NOT_ACTIVE`, not a
  replay; the original outcome stays visible in the event log.
- `agent.sampling` is optional and defaults to `{}`.
- `status_reason` is trimmed and truncated to 500 characters. The database
  allows it to change only together with a status transition.
- Until ADR-0010, the scenario catalog lists `S01`, `S02`, and `S03` with
  placeholder versions `Sxx/draft-0`. Runs created before ADR-0010 record
  that placeholder in their snapshot.
- `InMemoryLedger` computes its configuration hash over its own JSON text;
  only the PostgreSQL hash is canonical (`finmind_run_config_hash`).
- Tests: `RunLifecycleTests` (scenarios on both ledgers, invalid
  configurations, database protection as owner and as `finmind_api`,
  canonical hash, and cancellation racing transfers).

## Consequences

- A run's configuration is reproducible from its stored snapshot and hash.
- Agents and callers cannot choose opening funds or the transaction cap
  for managed runs.
- Stopping a run takes effect at the ledger immediately.
- Two kinds of run exist until the legacy path is removed. Reports and
  exports must include managed runs only.

## Acceptance tests

- Valid configuration creates a `DRAFT` run with a stored snapshot and hash
  that match on read; unknown fields, out-of-range values, unknown
  scenario, and unconfigured provider/model are `INVALID_RUN_CONFIG`.
- Every allowed transition succeeds and writes `RUN_STATUS_CHANGED`;
  every disallowed transition is `INVALID_RUN_TRANSITION` and changes
  nothing.
- Terminal runs reject all transitions and all money-moving operations.
- Money-moving operations before `RUNNING` or after cancellation are
  `RUN_NOT_ACTIVE`, recorded, and change nothing.
- Opening funding during initialization uses snapshot values; the direct
  funding endpoint rejects managed runs.
- PostgreSQL, as owner bypassing the application: changing the snapshot,
  the hash, or a terminal status fails.
- PostgreSQL, as `finmind_api`: cannot update snapshot columns.
- Legacy unmanaged runs behave exactly as in Phase 1 (existing tests pass).
- Concurrent cancel and transfer: either the transfer commits before
  cancellation, or it is `RUN_NOT_ACTIVE`; never both partially.
