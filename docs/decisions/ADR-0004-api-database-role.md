# FINMIND ADR 0004: Restricted API Database Role

- Status: Proposed
- Date: 2 November 2026
- Scope: Phase 1 local PostgreSQL ledger access

## Context

The local API currently uses the same PostgreSQL login that applies
migrations. That login owns database objects and has more authority
than the API needs. The agent must never receive either login.

## Decision

Use separate PostgreSQL logins:

- `finmind` remains the local database owner and migration login.
- `finmind_api` is the API and integration-test login. It does not
  own tables, inherit the owner role, or receive schema CREATE rights.
- Keep credentials in local environment configuration and CI secrets
  or disposable CI configuration. Do not place them in prompts,
  traces, browser code, or committed files.

Grant `finmind_api` only the following table operations:

| Table | Allowed |
|---|---|
| `experiment_runs` | SELECT, INSERT, UPDATE of `transaction_limit_sen` |
| `accounts` | SELECT, INSERT |
| `journal_entries` | SELECT, INSERT |
| `journal_postings` | SELECT, INSERT |
| `obligations` | SELECT, INSERT, UPDATE of `paid` and `paid_entry_id` |

Grant sequence usage only where generated posting IDs require it.
Do not grant table DELETE, TRUNCATE, general UPDATE, schema CREATE,
or object ownership.

`LockExistingRun` and `LockRun` may retain `FOR UPDATE`, because the
API already needs column UPDATE permission on `experiment_runs`.
`LoadAccountKinds` must stop using `FOR UPDATE` on `accounts`; the
run lock serializes finance mutations for that run.

The API remains a trusted component. This role limits damage from
its database login; it does not replace server-side authorization,
policy checks, journal validation, or the database integrity triggers.

## Local and CI setup

Apply schema migrations using the owner login. Create and grant the
restricted role afterward. Run the API and PostgreSQL integration
tests with the restricted login. Keep the test database separate
from the application database.

## Acceptance tests

- Funding, balance reads, transfer, savings, obligation creation,
  bill payment, and idempotent retries work as `finmind_api`.
- Concurrent spending and invoice creation remain serialized.
- Direct UPDATE and DELETE of journals or postings fail.
- Direct TRUNCATE and schema changes fail.
- The API login cannot change account rows or unrelated run fields.
- Rejected operations do not change balances or history.