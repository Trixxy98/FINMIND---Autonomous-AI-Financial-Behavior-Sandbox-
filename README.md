# FINMIND

A local-first research sandbox for observing AI behavior with virtual funds only. No real financial integrations are part of this project.

## Current status

Phase 0 scaffold is in place. Phase 1 has a PostgreSQL-backed C# ledger for the local API plus an in-memory ledger prototype for deterministic unit tests. The API connects with the restricted `finmind_api` database role (see `docs/decisions/ADR-0004-api-database-role.md`); migrations run as the owner role `finmind`. A server-stored per-run transaction cap is enforced. Every finance outcome has a stable status and reason code (see `docs/decisions/ADR-0005-policy-decisions-and-reason-codes.md`). Committed transfers, savings moves, and bill payments can be fully reversed by a researcher; reversing a bill payment returns its obligation to unpaid (see `docs/decisions/ADR-0006-reversal-aware-obligation-state.md`). Every finance decision and state transition on an existing run, including denials, is recorded in an append-only, hash-linked per-run event log, readable at `GET /internal/runs/{runId}/events` (see `docs/decisions/ADR-0007-finance-audit-event-store.md`). Managed runs are created from an immutable, hashed configuration snapshot (`POST /internal/runs`) and follow a server-enforced lifecycle; money moves only while a run is `RUNNING`, and opening funds come only from the snapshot (see `docs/decisions/ADR-0008-run-lifecycle-and-config-snapshot.md`). Activation materializes the run's scenario (versioned files in `scenarios/`, seeded with a specified PRNG) and starts a server-owned virtual clock (see `docs/decisions/ADR-0010-scenarios-prng-and-virtual-clock.md`); the API refuses to start if a scenario file is invalid. Runs created through the older `/funding` path are unmanaged development runs and are excluded from research results. Local internal API endpoints remain unauthenticated. Do not expose the internal endpoints outside localhost. No model-provider integration or agent tool gateway exists yet.

## Prerequisites

- .NET 10 SDK
- Python 3.9+ for the dependency-free runtime health endpoint (target deployment runtime may be upgraded separately)
- Node.js 20+ and npm
- Docker Desktop with Docker Compose v2

## Local setup

1. Copy `.env.example` to `.env` and replace the local development password.
2. Start PostgreSQL: `docker compose -f infra/compose.yaml up -d`.
3. Apply migrations in order as the owner role using
   `docker compose -f infra/compose.yaml exec -T postgres psql -v ON_ERROR_STOP=1 -U finmind -d finmind < MIGRATION_PATH`,
   replacing `MIGRATION_PATH` with each of:
   `infra/db/migrations/001_ledger.sql`,
   `infra/db/migrations/002_run_transaction_policy.sql`,
   `infra/db/migrations/003_obligation_invoice_identity.sql`,
   `infra/db/migrations/004_api_database_role.sql`,
   `infra/db/migrations/005_journal_reversal.sql`,
   `infra/db/migrations/006_run_events.sql`,
   `infra/db/migrations/007_run_event_lock_order.sql`,
   `infra/db/migrations/008_run_lifecycle.sql`,
   and `infra/db/migrations/009_scenarios_and_clock.sql`.
   Then set the restricted API role password interactively (the role is cluster-wide, so do this once):
   `docker compose -f infra/compose.yaml exec postgres psql -U finmind -d finmind -c "\password finmind_api"`.
   Use the same value as `Password=` in `ConnectionStrings__Finmind` and `FINMIND_API_TEST_DATABASE` in `.env`.
4. Start the API with the local connection string from `.env`: `set -a && source .env && set +a && dotnet run --project apps/api/Finmind.Api.csproj` (listens on `http://localhost:5080`).
5. Start the runtime health endpoint: `python3 services/agent-runtime/health_server.py` (listens on `http://localhost:8090`).
6. Start the web app: `cd apps/web && npm install && npm run dev` (listens on Vite's printed local URL).

Health checks: `GET http://localhost:5080/health` and `GET http://localhost:8090/health`.

## Checks

- API: `dotnet build apps/api/Finmind.Api.csproj`
- Finance ledger tests: `dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`
- PostgreSQL integration tests (use a separate database so tests do not add fixtures to the app database): create `finmind_test` with `docker compose -f infra/compose.yaml exec -T postgres createdb -U finmind finmind_test`, apply all migrations to it using the migration commands above with `-d finmind_test` (migration 004 grants `finmind_api` access to that database), then run `set -a && source .env && set +a && dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`. Tests use `FINMIND_TEST_DATABASE` (owner role) and `FINMIND_API_TEST_DATABASE` (restricted role); without them, the PostgreSQL tests are skipped.
- Runtime: `python3 -m unittest discover -s services/agent-runtime/tests -v`
- Web: `cd apps/web && npm test && npm run build`
- Compose configuration (requires running Docker daemon): `docker compose -f infra/compose.yaml config`

All credentials in `.env.example` are placeholders for local development only. Do not commit `.env`.
