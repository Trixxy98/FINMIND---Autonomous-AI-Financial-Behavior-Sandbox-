# FINMIND ADR 0007: Finance Audit Event Store

- Status: Implemented for Phase 1 local API
- Date: 20 November 2026
- Scope: Phase 1 durable, append-only, per-run event log for finance boundary decisions and state transitions

## Context

The specification requires every tool request, denial, state transition,
and outcome to be recorded in an append-only event store with a
monotonically increasing per-run sequence and tamper-evident hash linking
(sections 1, 11.1, 15; technical details section 6). ADR-0005 defined the
outcome and reason-code vocabulary but left decisions unpersisted.
ADR-0006 reserved `JOURNAL_REVERSED` and `OBLIGATION_PAYMENT_REVERSED`.

Today, a denied request leaves no durable trace, and the ledger is the only
history. Researchers cannot see attempts, only successful effects.

Technical details section 11 lists ADRs required before this phase. This
ADR decides four of them: event and ledger atomicity, append-only
enforcement for events, event hash canonicalization, and the Phase 1
redaction rule.

## Decision summary

- Add a `run_events` table: append-only, one ordered stream per run.
- Successful finance operations write their events **in the same database
  transaction** as the ledger change. No separate outbox: `run_events` is
  the durable record that later consumers (API reads, SSE, export) read by
  sequence cursor.
- Rejected operations roll back their ledger transaction, then append a
  `POLICY_DENIED` event in a **second, event-only transaction**.
- **No decision response without a durable event.** If the event cannot be
  written, the API returns `internal_error` instead of the decision. This
  is safe because the money effect is already rolled back (rejections) or
  never committed (commits share the transaction); the caller can retry,
  and retries cannot duplicate money effects.
- PostgreSQL assigns `sequence`, `prev_hash`, and `event_hash` in a
  trigger, so application code cannot choose or forge chain positions.
- The hash chain is tamper evidence only. It does not replace access
  control, backups, or external anchoring.
- Both ledgers emit the same event types and payloads for the same
  operation. `InMemoryLedger` keeps events in memory without hashes.

## Scope boundary

Phase 1 has no agent runtime, tool gateway, model calls, or virtual clock.
This ADR covers events produced by the **finance boundary** only.

| In scope now | Later |
|---|---|
| Finance decisions: commits, replays, rejections | `TOOL_CALL_PROPOSED`, `TOOL_RETURNED` (Phase 2 tool gateway) |
| State transitions: run, account, obligation, reversal | Model, memory, and scenario events (Phase 2) |
| Read API by sequence cursor | SSE stream (Phase 3) |
| Hash chain and verification function | Export format and external anchoring (Phase 4–5) |

When the Phase 2 gateway exists, a tool call's finance events will join the
spec's causal chain:
`TOOL_CALL_PROPOSED → POLICY_ALLOWED | POLICY_DENIED → LEDGER_COMMITTED → TOOL_RETURNED`.
The gateway will add the proposal and return events and a shared
`trace_id`. Phase 1 finance events reserve the `trace_id` column for this.

## Event taxonomy (event_version 1)

| Event type | Written when | Same transaction as change? |
|---|---|---|
| `RUN_CREATED` | `OpenRun` first creates a run and its transaction cap | Yes |
| `ACCOUNT_CREATED` | Account created (including treasury and cash at run creation) | Yes |
| `OBLIGATION_CREATED` | New obligation stored (not on invoice-reference replay) | Yes |
| `LEDGER_COMMITTED` | Any new journal, including opening funding and reversals | Yes |
| `OBLIGATION_PAID` | Obligation set to PAID by a bill payment | Yes |
| `JOURNAL_REVERSED` | A reversal journal is committed | Yes |
| `OBLIGATION_PAYMENT_REVERSED` | A bill-payment reversal returns an obligation to UNPAID | Yes |
| `LEDGER_REPLAYED` | An idempotent replay returns an existing journal | Yes (event-only transaction) |
| `POLICY_DENIED` | Outcome `malformed`, `denied`, or `conflict` | No: separate event-only transaction after rollback |
| `FINANCE_ERROR` | Outcome `internal_error` | No: separate, best effort |

`POLICY_DENIED` covers all three rejection outcomes and records which one
in its payload. It is named to match the spec taxonomy. It means "the
finance boundary did not commit this request". It does not mean the
caller intended a violation.

`POLICY_ALLOWED` is not emitted in Phase 1. The policy checks and the
commit are one step in the finance boundary, so `LEDGER_COMMITTED`
already implies the request was allowed. The Phase 2 gateway may add
`POLICY_ALLOWED` if policy evaluation becomes a separate step.

An obligation-creation replay (same invoice reference, same details) is a
no-op and writes no event. An invoice-reference conflict writes
`POLICY_DENIED`.

`FINANCE_ERROR` is best effort: if the failure is the database itself,
the event may also fail. It carries no exception text.

Reads (`GetBalance`, `GetObligation`, `GetJournals`, `GetEvents`) write no
events in Phase 1. Agent reads become tool-call events in Phase 2.

### Ordering within one operation

Events from one transaction get consecutive sequence numbers in this order:

1. `RUN_CREATED`, then `ACCOUNT_CREATED` (treasury, cash), for a new run.
2. `ACCOUNT_CREATED` or `OBLIGATION_CREATED` for setup operations.
3. `LEDGER_COMMITTED`.
4. `OBLIGATION_PAID`, or `JOURNAL_REVERSED` followed by
   `OBLIGATION_PAYMENT_REVERSED`.

## Payloads and redaction

Payloads are JSON objects with camelCase keys. They contain only
identifiers, amounts in sen, kinds, outcomes, reason codes, and
idempotency keys. They never contain connection strings, credentials,
exception text, stack traces, or SQL.

| Event type | Payload fields |
|---|---|
| `RUN_CREATED` | `transactionLimitSen` |
| `ACCOUNT_CREATED` | `accountId`, `kind` |
| `OBLIGATION_CREATED` | `obligationId`, `merchantAccountId`, `amountSen`, `invoiceReference` |
| `LEDGER_COMMITTED` | `journalId`, `kind`, `postings` (`accountId`, `amountSen`), `obligationId`, `reversesJournalId` |
| `OBLIGATION_PAID` | `obligationId`, `journalId` |
| `JOURNAL_REVERSED` | `originalJournalId`, `reversalJournalId`, `reason`, `initiator` |
| `OBLIGATION_PAYMENT_REVERSED` | `obligationId`, `originalJournalId`, `reversalJournalId` |
| `LEDGER_REPLAYED` | `journalId`, `effective`, `reversedByJournalId` |
| `POLICY_DENIED` | `operation`, `outcome`, `reasonCode`, `request` (operation inputs, below) |
| `FINANCE_ERROR` | `operation`, `reasonCode` |

`operation` is one of `open_run`, `create_account`, `create_obligation`,
`transfer`, `move_to_savings`, `pay_obligation`, `reverse_journal`, or
`commit` (the generic journal path used only by tests and internal code).
`request` holds that operation's typed inputs: account and obligation IDs,
`amountSen`, idempotency key, invoice reference, and reversal reason.
The human-readable exception `message` is not stored; `reasonCode` is
the contract.

Free text in Phase 1 is limited to invoice references and reversal
reasons, both entered by the researcher or scenario. Model-generated text
does not exist yet. Before Phase 2 stores agent text, a redaction rule for
it must be decided (separate ADR).

### Promoted columns

For querying without JSON parsing, these payload values are also stored as
columns: `operation`, `outcome`, `reason_code`, `journal_id`,
`obligation_id`, `idempotency_key`.

`journal_id` and `obligation_id` are foreign keys, so they are set only on
events about rows that exist. On `POLICY_DENIED` they stay NULL and the
requested IDs appear only in `payload.request`, because a rejected request
may name a journal or obligation that does not exist.

## Unrecordable requests

Events belong to a run (`run_id` is a foreign key). A request is not
recorded in `run_events` when:

- `runId` is empty (`MISSING_REQUIRED_FIELD` on run ID), or
- the run does not exist (`RUN_NOT_FOUND`).

These are written to the server log with the reason code only. The
in-memory ledger follows the same rule: it records events only for runs
it has opened.

## Actor

Phase 1 has no authentication. Every event records `source = 'internal_api'`
for API calls and `source = 'scenario_engine'` for reversals initiated by
the scenario engine through the ledger. `agent` is reserved for Phase 2.
This is a description of the path, not a verified identity. Authentication
work must replace it with an authenticated principal.

## Schema (migration 006)

`run_events`:

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Assigned by application |
| `run_id` | UUID NOT NULL | FK `experiment_runs(id)` |
| `sequence` | BIGINT NOT NULL | Assigned by trigger; UNIQUE (`run_id`, `sequence`) |
| `event_type` | TEXT NOT NULL | CHECK against the taxonomy above |
| `event_version` | INT NOT NULL | 1 for all types in this ADR |
| `occurred_at` | TIMESTAMPTZ NOT NULL | Set by trigger: transaction wall time (UTC) |
| `virtual_time` | TIMESTAMPTZ NULL | NULL until the virtual clock exists |
| `source` | TEXT NOT NULL | `internal_api`, `scenario_engine`; `agent` reserved |
| `trace_id` | TEXT NULL | Reserved for Phase 2 |
| `operation`, `outcome`, `reason_code`, `idempotency_key` | TEXT NULL | Promoted from payload |
| `journal_id` | UUID NULL | FK to `journal_entries` in the same run |
| `obligation_id` | UUID NULL | FK to `obligations` in the same run |
| `payload` | JSONB NOT NULL | Object only |
| `prev_hash` | BYTEA NULL | NULL only for sequence 1 |
| `event_hash` | BYTEA NOT NULL | SHA-256 |

Rules:

- Append-only: a `BEFORE UPDATE OR DELETE` trigger rejects all changes,
  using the same mechanism as journals (ADR-0001).
- `finmind_api` gets SELECT and INSERT only. No UPDATE, DELETE, TRUNCATE.
- A `BEFORE INSERT` trigger:
  1. Locks the run row (`SELECT ... FOR UPDATE`), so sequence assignment
     is serialized for every writer, including event-only transactions.
     This is re-entrant for finance operations that already hold it.
  2. Sets `sequence = previous max + 1` and `prev_hash = previous
     event_hash` for the run.
  3. Sets `occurred_at = now()`.
  4. Computes `event_hash` (below).
  Values supplied by the caller for these columns are ignored.

Lock order is always: run row, then sequence assignment.

Amendment (migration `007_run_event_lock_order.sql`, 21 November 2026):
migration 006 used a per-run advisory lock here instead. The Phase 1
gate's race test (a new payment racing the reversal of the old payment)
found a deadlock: the reversal held the run row and waited for the
advisory lock, while the event-only `POLICY_DENIED` transaction held the
advisory lock and waited for the run row in its foreign key check.
PostgreSQL aborted the denial write, so the API would have returned
`internal_error`. Migration 007 replaces the advisory lock with the run
row lock so every writer uses the same order.

## Hash canonicalization (v1)

```
event_hash = sha256(convert_to(
  'finmind-event-v1' || E'\n' ||
  coalesce(encode(prev_hash, 'hex'), '') || E'\n' ||
  run_id::text || E'\n' ||
  sequence::text || E'\n' ||
  event_type || E'\n' ||
  event_version::text || E'\n' ||
  to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || E'\n' ||
  source || E'\n' ||
  payload::text,
  'UTF8'))
```

- Uses PostgreSQL's built-in `sha256()`; no extension needed.
- `payload::text` is PostgreSQL's deterministic JSONB text form. Exports
  must include this exact text so an external verifier can recompute the
  hash.
- Promoted columns are copies of payload values and are not hashed
  separately.
- The `finmind-event-v1` prefix versions the scheme. A future change uses
  a new prefix; old events keep their original scheme.

A SQL function `finmind_verify_run_events(run_id)` recomputes the chain
and returns the first sequence whose hash or link does not match, or NULL
if the chain is intact.

## Implementation shape

- `PostgresLedger` appends events through one internal helper using the
  operation's open transaction.
- Each public mutating method runs inside a wrapper. On `LedgerException`:
  roll back, then (if the run is recordable) open a new transaction, append
  `POLICY_DENIED`, commit, and rethrow the original exception.
- `IFinanceLedger` gains `GetEvents(runId, afterSequence, limit)`.
  `InMemoryLedger` stores events in memory with the same types, payloads,
  and order (no hashes).
- The API adds `GET /internal/runs/{runId}/events?afterSequence=0&limit=100`,
  returning events in sequence order with `nextAfterSequence`. A negative
  `afterSequence` is treated as 0; `limit` defaults to 100 and is clamped
  to 1..500.

## Implementation notes

- Migration: `infra/db/migrations/006_run_events.sql` (re-runnable). It also
  adds `UNIQUE (id, run_id)` on `journal_entries` for the event foreign key.
- Event types and payloads: `apps/api/Finance/RunEvents.cs`
  (`FinanceEvents`), shared by both ledgers.
- Each public mutating ledger method wraps a private `*Core` method with
  `Record`. Checked-arithmetic overflow is converted to `INVALID_AMOUNT`
  inside the ledger so it is recorded like other rejections.
- `RUN_CREATED` is written when `OpenRun` inserts a new run, or sets the
  cap on a legacy run that had none.
- Tests: `EventTests` (flow, unknown runs, concurrency, chain verification
  and tamper detection, database-assigned chain fields, rollback, and
  restricted-role permissions) and an extension of `ReasonCodeTests` that
  checks each rejection writes exactly one `POLICY_DENIED`.
- Not covered by an automated test: the path where the `POLICY_DENIED`
  write itself fails and the API returns `internal_error`. It needs fault
  injection that the current test setup does not have.

## Consequences

- Every attempt on an existing run becomes visible, not only successful
  effects. Denials and replays become countable for metrics such as the
  Unauthorized Tool Attempt Rate (with denominators).
- Rejections cost one extra transaction.
- Writing an event can now fail a request that previously succeeded. This
  is intended: the request is not reported as done unless it is recorded.
- The ledger and event log are both authoritative for their own facts:
  the ledger for money, the event log for what was requested and decided.
  A `LEDGER_COMMITTED` event must always match a journal; tests check this.

## Acceptance tests

Both ledgers unless noted:

- Each mutating operation produces the documented events, in order, with
  the documented payload fields.
- Each rejection scenario from `ReasonCodeTests` produces exactly one
  `POLICY_DENIED` with the matching outcome and reason code, and no other
  events.
- Replays produce `LEDGER_REPLAYED` with correct `effective` and
  `reversedByJournalId`.
- Requests with an empty or unknown run ID produce no events.
- Sequences are 1..n with no gaps per run; runs do not see each other's
  events.
- Every `LEDGER_COMMITTED` matches an existing journal with the same
  postings, and every journal has exactly one `LEDGER_COMMITTED`.
- Events contain no exception message text.
- PostgreSQL: concurrent operations on one run produce a gap-free sequence
  and a valid chain.
- PostgreSQL: `finmind_verify_run_events` returns NULL for an untouched
  run and detects a payload changed with triggers disabled (inside a
  rolled-back transaction).
- PostgreSQL: `finmind_api` cannot UPDATE, DELETE, or TRUNCATE
  `run_events`; caller-supplied `sequence` or hashes are overwritten.
- PostgreSQL: if the ledger transaction fails at commit, neither the
  journal nor its events exist.
- API: event read endpoint returns ordered pages and stops at `limit`.
