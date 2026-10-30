# FINMIND

A local-first research sandbox for observing AI behavior with virtual funds only. No real financial integrations are part of this project.

## Current status

Phase 0 scaffold is in place. Phase 1 now has a PostgreSQL-backed C# ledger for the local API plus an in-memory ledger prototype for deterministic unit tests. Local internal API endpoints remain unauthenticated, and server-side run lifecycle/policy and durable audit event stream are not implemented. Do not expose the internal endpoints outside localhost. No model-provider integration or agent tool gateway exists yet.

## Prerequisites

- .NET 10 SDK
- Python 3.9+ for the dependency-free runtime health endpoint (target deployment runtime may be upgraded separately)
- Node.js 20+ and npm
- Docker Desktop with Docker Compose v2

## Local setup

1. Copy `.env.example` to `.env` and replace the local development password.
2. Start PostgreSQL: `docker compose -f infra/compose.yaml up -d`.
3. Apply migrations in order: `docker compose -f infra/compose.yaml exec -T postgres psql -v ON_ERROR_STOP=1 -U finmind -d finmind < infra/db/migrations/001_ledger.sql` and then `docker compose -f infra/compose.yaml exec -T postgres psql -v ON_ERROR_STOP=1 -U finmind -d finmind < infra/db/migrations/002_run_transaction_policy.sql`.
4. Start the API with the local connection string from `.env`: `set -a && source .env && set +a && dotnet run --project apps/api/Finmind.Api.csproj` (listens on `http://localhost:5080`).
5. Start the runtime health endpoint: `python3 services/agent-runtime/health_server.py` (listens on `http://localhost:8090`).
6. Start the web app: `cd apps/web && npm install && npm run dev` (listens on Vite's printed local URL).

Health checks: `GET http://localhost:5080/health` and `GET http://localhost:8090/health`.

## Checks

- API: `dotnet build apps/api/Finmind.Api.csproj`
- Finance ledger tests: `dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`
- PostgreSQL integration tests (use a separate database so tests do not add fixtures to the app database): create `finmind_test` with `docker compose -f infra/compose.yaml exec -T postgres createdb -U finmind finmind_test`, apply both migrations to it using the migration commands above with `-d finmind_test`, then run `set -a && source .env && set +a && dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`.
- Runtime: `python3 -m unittest discover -s services/agent-runtime/tests -v`
- Web: `cd apps/web && npm test && npm run build`
- Compose configuration (requires running Docker daemon): `docker compose -f infra/compose.yaml config`

All credentials in `.env.example` are placeholders for local development only. Do not commit `.env`.
