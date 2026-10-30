# FINMIND ADR 0002: Server-Owned Per-Run Transaction Limits

- Status: Accepted for Phase 1 local API
- Date: 30 October 2026
- Scope: Prevent action callers from choosing or raising their own transaction cap

## Context

The API action request previously included `TransactionLimitSen`. A caller could therefore choose a permissive limit, defeating the intended transaction-limit check. FINMIND needs repeatable experiment configuration and server-side enforcement.

## Decision

- Store a positive `transaction_limit_sen` on each run during run initialization.
- Treat the value as run policy: initialize it once and reject attempts to change it afterward.
- Do not accept a transaction limit in transfer, savings, or bill-payment action requests. The finance boundary loads the stored value and enforces it within the run-serialized database transaction.
- Exclude opening funding from the per-action cap; apply the cap to transfer, savings movement, and bill payment.
- Existing runs with no stored cap fail closed for money mutations. Do not invent a migration-time cap for them.
- The local run-initialization endpoint remains unauthenticated development scaffolding and must stay bound to localhost; it is not yet a trusted researcher authorization surface.

## Consequences

The policy is persisted and reproducible per run, so action attempts cannot silently override it. Existing runs created before this policy migration need a new run if they already have opening funding. PostgreSQL role separation, authenticated experiment setup, and immutable full run snapshots remain later work.

## Implementation

`infra/db/migrations/002_run_transaction_policy.sql` adds the column and database trigger. `PostgresLedger` writes it during `OpenRun`, prevents later changes, and applies it to committed action journals. The API funding/run-initialization request accepts the limit; transfer, savings, and bill-payment DTOs do not. A legacy run without a policy may only be initialized if it has no prior journal activity; an existing active run must fail closed rather than accept a new policy or opening allocation.
