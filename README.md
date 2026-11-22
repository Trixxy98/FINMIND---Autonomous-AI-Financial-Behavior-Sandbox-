# FINMIND

A local-first research sandbox for observing AI behavior with virtual funds only. No real financial integrations are part of this project.

## Current status

Phase 0 scaffold is in place. Phase 1 has a PostgreSQL-backed C# ledger for the local API plus an in-memory ledger prototype for deterministic unit tests. The API connects with the restricted `finmind_api` database role (see `docs/decisions/ADR-0004-api-database-role.md`); migrations run as the owner role `finmind`. A server-stored per-run transaction cap is enforced. Every finance outcome has a stable status and reason code (see `docs/decisions/ADR-0005-policy-decisions-and-reason-codes.md`). Committed transfers, savings moves, and bill payments can be fully reversed by a researcher; reversing a bill payment returns its obligation to unpaid (see `docs/decisions/ADR-0006-reversal-aware-obligation-state.md`). Every finance decision and state transition on an existing run, including denials, is recorded in an append-only, hash-linked per-run event log, readable at `GET /internal/runs/{runId}/events` (see `docs/decisions/ADR-0007-finance-audit-event-store.md`). Managed runs are created from an immutable, hashed configuration snapshot (`POST /internal/runs`) and follow a server-enforced lifecycle; money moves only while a run is `RUNNING`, and opening funds come only from the snapshot (see `docs/decisions/ADR-0008-run-lifecycle-and-config-snapshot.md`). Activation materializes the run's scenario (versioned files in `scenarios/`, seeded with a specified PRNG) and starts a server-owned virtual clock (see `docs/decisions/ADR-0010-scenarios-prng-and-virtual-clock.md`); the API refuses to start if a scenario file is invalid. Runs created through the older `/funding` path are unmanaged development runs and are excluded from research results. An agent runtime acts only through the worker interface (`/internal/worker/...`): a worker credential (`FINMIND_WORKER_TOKEN`, at least 32 characters) claims a queued run, and a short-lived run-scoped session token is the only credential for that run's 11 allowlisted tools. Every tool call is recorded before and after evaluation (see `docs/decisions/ADR-0009-worker-and-tool-gateway.md`). The Python worker (`services/agent-runtime`) drives runs with deterministic mock agents; wall-time and token budgets are enforced by the server (see `docs/decisions/ADR-0011-runtime-loop-and-mock-agent.md`). Claude (Anthropic) is integrated as the first real provider with a server-enforced cost budget, versioned prompts, and no automatic model fallback (see `docs/decisions/ADR-0012-claude-provider-adapter.md`). CI never calls a paid API. Researcher endpoints remain unauthenticated localhost scaffolding. Do not expose the internal endpoints outside localhost.

## Prerequisites

- .NET 10 SDK
- Python 3.10+ for the agent runtime (mock agents use only the standard library; Claude runs need `pip install -r services/agent-runtime/requirements.txt`)
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
   `infra/db/migrations/009_scenarios_and_clock.sql`,
   `infra/db/migrations/010_worker_and_tool_gateway.sql`,
   `infra/db/migrations/011_model_invocations_and_budgets.sql`,
   `infra/db/migrations/012_model_cost_budget.sql`,
   and `infra/db/migrations/013_cost_reservations_and_recovery.sql`.
   Then set the restricted API role password interactively (the role is cluster-wide, so do this once):
   `docker compose -f infra/compose.yaml exec postgres psql -U finmind -d finmind -c "\password finmind_api"`.
   Use the same value as `Password=` in `ConnectionStrings__Finmind` and `FINMIND_API_TEST_DATABASE` in `.env`.
4. Start the API with the local connection string from `.env`: `set -a && source .env && set +a && dotnet run --project apps/api/Finmind.Api.csproj` (listens on `http://localhost:5080`).
5. Start the runtime health endpoint: `python3 services/agent-runtime/health_server.py` (listens on `http://localhost:8090`).
   Run the agent worker (needs `FINMIND_WORKER_TOKEN` in `.env`, at least 32 characters, shared with the API): `set -a && source .env && set +a && cd services/agent-runtime && python3 -m finmind_runtime.worker` (add `--once` to run a single queued run). Create and start a run with `POST /internal/runs` and `POST /internal/runs/{runId}/start`; the worker claims it.
6. Start the web app: `cd apps/web && npm install && npm run dev` (listens on Vite's printed local URL).

Health checks: `GET http://localhost:5080/health` and `GET http://localhost:8090/health`.

## Running with Claude

Claude runs spend real money. The Anthropic credential belongs only to the worker: do not put `ANTHROPIC_API_KEY` in the `.env` file the API loads. Either run `ant auth login` once (the SDK picks up the profile) or export the key only in the worker's shell. Then, from `services/agent-runtime`:

1. `python3 -m pip install -r requirements.txt`
2. Try one short run first: `FINMIND_API_URL=http://127.0.0.1:5080 FINMIND_WORKER_TOKEN=... python3 scripts/smoke_claude.py` (S01, 3 virtual days, US$0.50 budget; prints the cost and trace checks). A request is sent only if its worst case fits in the remaining budget, calculated from this repository's price table and Anthropic's token-count estimate plus a margin. It is not a billing limit: also set a spend limit in the Anthropic Console.
3. For experiments, create runs with `agent.provider` `anthropic`, a model ID (`claude-opus-5-5`, `claude-sonnet-5-5`, or `claude-haiku-5-5`), `agent.settings.effort`, and `maxCostMicroUsd`, then run the worker as above. Verify current prices before larger batches; the pricing table is in `apps/api/Finance/ProviderCatalogs.cs`.

## Checks

- API: `dotnet build apps/api/Finmind.Api.csproj`
- Finance ledger tests: `dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`
- PostgreSQL integration tests (use a separate database so tests do not add fixtures to the app database): create `finmind_test` with `docker compose -f infra/compose.yaml exec -T postgres createdb -U finmind finmind_test`, apply all migrations to it using the migration commands above with `-d finmind_test` (migration 004 grants `finmind_api` access to that database), then run `set -a && source .env && set +a && dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`. Tests use `FINMIND_TEST_DATABASE` (owner role) and `FINMIND_API_TEST_DATABASE` (restricted role); without them, the PostgreSQL tests are skipped.
- Runtime: `python3 -m unittest discover -s services/agent-runtime/tests -v`. End-to-end tests run only when the API is running and `FINMIND_E2E_API_URL` and `FINMIND_WORKER_TOKEN` are set (CI does this against `finmind_test`).
- Web: `cd apps/web && npm test && npm run build`
- Compose configuration (requires running Docker daemon): `docker compose -f infra/compose.yaml config`

All credentials in `.env.example` are placeholders for local development only. Do not commit `.env`.
