# FINMIND Technical Details

This document expands the system specification into implementation-facing boundaries and acceptance rules. The source specification remains `FINMIND_Full_System_Documentation.md`; where a detail is not settled there, this document marks it as a decision to make rather than silently inventing product behavior.

## 1. System boundaries

### Trusted components
- Finance/policy service: authoritative authorization and financial state transition boundary.
- Database: durable journals, postings, run-scoped records, and audit events.
- Runtime host: controls model calls, tool dispatch, budgets, and run lifecycle.
- API: authenticates users and scopes all read/control requests.

### Untrusted inputs
- Model output and tool arguments.
- Scenario messages, invoice descriptions, offers, and simulated user content.
- Browser input, imported scenario files, and exported artifacts when re-imported.

Untrusted content is data, never executable instruction. It cannot alter roles, policy, run identity, account ownership, or tool grants.

## 2. Money and ledger contract

- Canonical amount: signed integer number of sen; currency is explicit and MVP accepts `MYR` only.
- Parse and validate at the boundary; never use binary floating point for money.
- Each committed journal has at least two postings, all in one run and currency, whose signed sum is exactly zero.
- Journal creation and postings commit atomically. An incomplete journal is not visible as committed.
- Opening funds are a journal from simulator treasury to the run's agent cash account.
- A run accepts only one opening-funding journal; retries with the original idempotency key return the original journal, while a new funding key cannot create a second opening allocation.
- Transfers and savings moves must identify both permitted accounts and preserve total value, except explicit authorized synthetic source/sink events.
- Journal kind constrains its posting account kinds; a generic transfer must never debit simulator treasury or otherwise act as an opening-funding/mint path. Validate this in the trusted finance boundary, not only in tool/API handlers.
- A bill payment must bind to a server-known outstanding obligation, amount, payee, run, and idempotency key. Client/model-supplied labels do not resolve destinations.
- Derived balances are rebuildable projections. Reconciliation compares them with the sum of immutable postings.
- Reversal is a new linked journal; committed history is never edited or deleted.
- Enforce no overdraft unless a versioned experiment policy explicitly enables it.

### Initial PostgreSQL migration status
`infra/db/migrations/001_ledger.sql` creates the initial run/account/journal/posting/obligation schema. Composite foreign keys keep postings within one run and currency; deferred constraint triggers require exactly two distinct, balanced postings with account kinds allowed by journal kind; unique constraints enforce run-scoped idempotency and one opening-funding journal per run; journal entries and postings are append-only. The local API now uses `PostgresLedger`; `InMemoryLedger` remains for deterministic unit tests. PostgreSQL-backed transactions serialize mutations per run and validate available funds in the application. Database-role separation, caller authorization, run lifecycle/policy, and durable audit events remain unimplemented, so the internal API must remain localhost-only.

### Commit procedure
1. Authenticate trusted caller and resolve scoped run principal server-side.
2. Validate schema, run state, tool grant, ownership, currency, amount, destination, limits, and idempotency.
3. Lock or otherwise serialize relevant account/obligation state.
4. If an idempotency key already committed, return the original outcome without a second effect.
5. Build balanced postings and validate invariants inside the database transaction.
6. Commit journal and state projection atomically; append associated audit events consistently.
7. Return a structured result that says whether it committed and identifies the journal only when applicable.

## 3. Policy decision contract

Every proposal receives a durable decision with a stable reason code. Minimum outcomes: allowed, denied, malformed, unsupported capability, duplicate/replayed, and internal error. A denial is not a financial commit. Do not infer intent from the reason code.

Policy inputs include run lifecycle, actor scope, tool grant/version, account ownership, destination allowlist, obligation state, amount and action limits, available balance, currency, idempotency status, and global run budgets. The model cannot provide authoritative values for these fields.

## 4. Agent/runtime contract

- The model sees a bounded observation, objective, current virtual time, and versioned tool schemas.
- Valid control actions include wait, ask for details, submit a plan, finish, or call an available tool; a transaction is never mandatory per turn.
- Tool adapter accepts only strict typed arguments. Unknown tools are logged as unsupported attempts, not dynamically executed.
- The host associates every call with an immutable run/agent identity; never trust a model-provided actor ID.
- On timeout after possible commit, query by idempotency key before retrying or reporting failure.
- The deterministic mock must support scripted success, denial, malformed call, timeout/retry, and budget exhaustion cases.
- Provider credentials stay outside prompts, traces, exports, browser bundles, and agent-readable configuration.

## 5. Run lifecycle and reproducibility

A run snapshots objective, initial capital, duration, action/token budgets, model/provider metadata, sampling parameters, tool schema version, policy version, scenario version, seed, and memory mode. Mutating the template later must not mutate an existing run.

Lifecycle transitions are validated server-side. Cancellation is cooperative but bounded; a cancelled run must not accept new tool proposals. Terminal states are immutable except for separately recorded administrative annotations.

Record both UTC wall time and virtual time. Scenario randomness must be seeded and versioned. Replaying or exporting a run must not mutate the original run or ledger.

## 6. Event and trace contract

Each run has monotonically increasing sequence numbers. Events should include run/agent IDs, trace ID, wall and virtual timestamps, event type/version, redacted payload, and relevant policy/journal/model metadata. Event delivery may be at-least-once; consumers deduplicate by stable event identity/sequence.

Minimum causal sequence for a tool action:
`TOOL_CALL_PROPOSED → POLICY_ALLOWED|POLICY_DENIED → LEDGER_COMMITTED (only if committed) → TOOL_RETURNED`.

Persist both proposal and outcome. Never fabricate a commit event from model prose. Redact secrets and unnecessary personal-like data before persistence/export. Hash chaining is tamper evidence only; access control and backups remain necessary.

## 7. API and UI semantics

- Every run endpoint checks user role and run ownership, not merely UUID possession.
- Internal tool dispatch is not a public arbitrary endpoint; it accepts only authenticated worker identities with short-lived run scope.
- Pagination uses stable cursors; SSE resumes from a sequence cursor and handles duplicate delivery.
- Wallet reads identify projection/version and expose reconciliation status where available.
- UI labels are explicit: PROPOSED, DENIED, COMMITTED, REPORTED BY AGENT, VERIFIED BY LEDGER.
- Agent text is never presented as authoritative wallet data. Show source and timestamp for every displayed balance.
- Export includes schema version, run snapshot, event/action trace, ledger data, metric version, and redaction notes.

## 8. Metrics and interpretation

Every metric definition is versioned and includes numerator, denominator, exclusions, and source events. Report raw counts alongside rates. Separate attempted violations from actual integrity failures. A financial integrity violation is a platform defect; a denied request is an observed attempt, not a successful exploit. Behavioral labels require rule IDs and evidence; ambiguous cases remain UNKNOWN_REVIEW pending human adjudication.

Comparisons should match scenario, seed, tools, policy, budgets, and other controls except the explicitly varied factor. Record model snapshot where available and acknowledge provider nondeterminism. Do not generalize from one run or describe operational behavior as emotion, motive, or intrinsic character.

## 9. Security and operations checklist

- Default-deny tool policy and server-side argument validation.
- Run and tenant scoping on every read and write.
- Separate least-privilege DB roles for API, worker, and migrations.
- No agent-controlled SQL, shell, code execution, filesystem access, or arbitrary URL fetch.
- Egress restricted to approved model endpoints; block private network and metadata access.
- Resource limits, rate limits, token/tool/action budgets, cancellation, kill switch.
- Secrets managed server-side; logs and exports redacted.
- Safe retry and crash recovery tested around commit/response boundaries.
- Synthetic data only; local prototype is not represented as production-secure.
- Backups, retention, access audit, and export authorization documented before shared deployment.

## 10. Test matrix

| Area | Required cases |
|---|---|
| Money parsing | zero, negative, overflow, malformed, valid sen; no float conversion |
| Ledger | balanced commit; unbalanced rejected; partial journal cannot commit; reversal linked |
| Authorization | wrong role, wrong run, wrong account, unsupported tool, revoked grant |
| Limits | insufficient balance, per-transaction limit, action/token/time budget |
| Idempotency | same key same outcome; conflicting payload rejected; timeout-after-commit retry once |
| Concurrency | simultaneous spends cannot overdraw; duplicate obligation cannot pay twice |
| Events | ordered sequence; denial recorded; commit linked; secrets redacted; replay is read-only |
| Runtime | mock wait/finish/tool calls; malformed output; provider timeout; cancellation |
| Research | seeded fixture repeatability; matched comparisons; metric denominators and version |
| UI/API | authorization, labels, pagination/SSE resume, verified balance provenance |

## 11. Decisions requiring explicit ADRs

Before the relevant phase, document decisions for: database isolation/locking strategy; event + ledger atomicity/outbox strategy; exact append-only enforcement mechanism; event hash canonicalization; authentication and deployment boundary; scenario schema and PRNG; savings liquidity semantics; provider and cost metering; retention/redaction; and exact supported dependency versions. Avoid choosing infrastructure beyond the documented MVP need.
