# FINMIND

A local-first research sandbox for observing AI behavior with virtual funds only. No real financial integrations are part of this project.

## Current status

Phase 0 scaffold is in place. Phase 1 has an in-memory C# ledger prototype and local internal API endpoints for virtual opening funding, balance reads, transfers, savings moves, synthetic obligations, and payments against server-stored obligation amounts. This is development-only: state is lost on API restart, endpoints have no authentication, and there is no PostgreSQL persistence, server-side run lifecycle/policy, or durable audit event stream. Do not expose the internal endpoints outside localhost. No model-provider integration or agent tool gateway exists yet.

## Prerequisites

- .NET 10 SDK
- Python 3.9+ for the dependency-free runtime health endpoint (target deployment runtime may be upgraded separately)
- Node.js 20+ and npm
- Docker Desktop with Docker Compose v2

## Local setup

1. Copy `.env.example` to `.env` and replace the local development password.
2. Start PostgreSQL: `docker compose -f infra/compose.yaml up -d`.
3. Start the API: `dotnet run --project apps/api/Finmind.Api.csproj` (listens on `http://localhost:5080`).
4. Start the runtime health endpoint: `python3 services/agent-runtime/health_server.py` (listens on `http://localhost:8090`).
5. Start the web app: `cd apps/web && npm install && npm run dev` (listens on Vite's printed local URL).

Health checks: `GET http://localhost:5080/health` and `GET http://localhost:8090/health`.

## Checks

- API: `dotnet build apps/api/Finmind.Api.csproj`
- Finance ledger tests: `dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj`
- Runtime: `python3 -m unittest discover -s services/agent-runtime/tests -v`
- Web: `cd apps/web && npm test && npm run build`
- Compose configuration (requires running Docker daemon): `docker compose -f infra/compose.yaml config`

All credentials in `.env.example` are placeholders for local development only. Do not commit `.env`.
