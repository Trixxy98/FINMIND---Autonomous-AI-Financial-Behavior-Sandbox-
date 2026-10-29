# FINMIND Ledger ADR 0001: Initial Trusted Ledger Boundary

- Status: Accepted for Phase 1 prototype
- Date: 29 October 2026
- Scope: First executable money-core milestone; not a production persistence design

## Context

FINMIND requires run-scoped virtual accounts, integer-sen postings, balanced immutable journals, no overdrafts for agent cash/savings, atomic commit, and idempotent retries. The product specification calls for PostgreSQL persistence and database-enforced integrity. The current Phase 0 API has no database access or finance domain.

## Decision

Start Phase 1 with a dependency-free in-memory ledger behind a small C# domain API. Serialize ledger operations with one process-local lock so the prototype can validate transaction atomicity, idempotency, run isolation, balance checks, and concurrency behavior without requiring a running Docker/PostgreSQL service. Keep account and journal identifiers server-generated. The mock ledger is not durable and is not an API or agent tool surface.

## Deferred decisions

This prototype does not satisfy durable persistence, database role separation, append-only DB enforcement, obligation uniqueness, bill-pay authorization, configured transaction limits, or cross-process concurrency. Before replacing the prototype with PostgreSQL, define migrations, schema constraints/triggers, transaction isolation/locking, and outbox/event atomicity. Do not expose this implementation as shared or production storage.

## Consequences

The in-memory model makes domain invariants testable now, but restarts lose all state and multiple API processes would not share a consistent ledger. Treat PostgreSQL persistence and database-backed concurrency tests as required remaining Phase 1 work.

## Phase 1 progress

The API now exposes local internal endpoints for run initialization, account creation, ledger balance reads, transfer, savings movement, synthetic obligation creation/reads, and payment by obligation ID. Payment amount and merchant are resolved from the run-scoped server-side obligation store rather than supplied as approval claims by the payment request. These endpoints are unauthenticated development scaffolding and MUST NOT be exposed beyond localhost or treated as an agent tool gateway. Production/shared use requires authenticated worker identity, policy/run lifecycle checks, persistent PostgreSQL transactions, and audit events.
