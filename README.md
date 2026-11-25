# FINMIND

A local research sandbox for observing how AI agents handle money, using
virtual Malaysian ringgit only. An agent works through a few typed tools;
every proposal, denial, ledger commit, and statement is recorded, and the
double-entry ledger is the only source of balances.

FINMIND is not a bank, wallet, payment service, broker, or source of
financial advice. No real money, real account, or real destination is
connected, and none may be added (`AGENTS.md`).

## Documentation

| Read this | For |
|---|---|
| `docs/ARCHITECTURE.md` | Components, trust boundaries, a run step by step, where each invariant is enforced |
| `docs/RESEARCHER_GUIDE.md` | Designing a comparison, reading evidence labels, review, export, writing up |
| `docs/reports/TEMPLATE.md` | Report template |
| `docs/security/threat-model.md` | Threats, controls, the tests behind them, remaining risk |
| `docs/decisions/` | ADR-0001 to ADR-0017: why each part works as it does |
| `docs/reports/` | Exit gate reports |
| `samples/README.md` | Sample exports of three mock runs, and how to verify them |

## Status

The ledger, run lifecycle, scenarios (S01 to S07, S09, S12), worker with
deterministic mock agents, the Claude adapter with a cost budget, the research
API, metrics, comparison, classification with human review, export, replay,
the web app with a pixel-art view, the operator stop, and a crash drill are
in place. CI runs every test with mock agents and never calls a paid API.

## Prerequisites

- .NET 10 SDK
- Python 3.10 or later (mock agents use only the standard library)
- Node.js 20 or later, with npm
- Docker with Compose v2

## Demo in one command

After step 1 below (environment files), this starts PostgreSQL, creates its
own `finmind_demo` database, applies the migrations, builds and starts the
API, runs three mock sample runs, and starts the web app. It prints the
address and a researcher token made for the demo. Ctrl-C stops it.

```sh
python3 services/agent-runtime/scripts/demo.py
```

It never touches any other database: it drops and recreates `finmind_demo`
only if that database carries the demo's marker. `--no-ui` runs the samples
and stops. Ports 5080 and 5173 must be free.

## Quickstart: a first mock run

Run every command from the repository root.

**1. Environment files.** Each process gets its own file. Copy the examples
and replace every placeholder; `openssl rand -hex 24` makes a token. The two
tokens must differ.

```sh
cp .env.example .env               # PostgreSQL owner (Compose)
cp .env.api.example .env.api       # API: restricted database role, worker token, researcher token
cp .env.worker.example .env.worker # worker: API address and worker token only
cp .env.test.example .env.test     # tests only
```

Use the same worker token in `.env.api` and `.env.worker`. Choose a password
for the `finmind_api` role now and put it in `.env.api` and `.env.test`.

**2. Database.** Start PostgreSQL (bound to `127.0.0.1` only), apply every
migration in order as the owner, then set the restricted role's password
(you are asked for it; it is never on the command line).

```sh
docker compose --env-file .env -f infra/compose.yaml up -d --wait
for f in infra/db/migrations/*.sql; do
  docker compose --env-file .env -f infra/compose.yaml exec -T postgres \
    psql -v ON_ERROR_STOP=1 -U finmind -d finmind < "$f" || break
done
docker compose --env-file .env -f infra/compose.yaml exec postgres psql -U finmind -d finmind -c "\password finmind_api"
```

`--env-file .env` matters: without it, Compose looks for `.env` in `infra/`
and uses its built-in placeholder password.

**3. API** (first shell). Listens on `127.0.0.1:5080`.

```sh
set -a && source .env.api && set +a && dotnet run --project apps/api/Finmind.Api.csproj
```

It refuses to start if PostgreSQL is down, if it connects as a privileged
role, if an Anthropic credential is in its environment, or if the tokens are
unsafe.

**4. Worker** (second shell). Claims queued runs and drives them.

```sh
set -a && source .env.worker && set +a && cd services/agent-runtime && python3 -m finmind_runtime.worker
```

**5. Web app** (third shell). Listens on `127.0.0.1:5173` and forwards `/api`
to the API.

```sh
cd apps/web && npm ci && npm run dev
```

**6. First run.** Open `http://127.0.0.1:5173`, paste the researcher token
(kept in memory only; a reload asks again), choose **New run**, keep the
defaults (S01, `finmind-mock-v1`), and start it. The run page follows it live:
actions with evidence labels, the ledger inspector, events, metrics, review,
replay, and the scene.

Health: `http://127.0.0.1:5080/health`. To stop every run at once, use
**Stop all runs** under the header (a reason is required).

## Running with Claude

Claude runs spend real money. The Anthropic credential belongs to the worker
only; the API refuses to start with it. Either run `ant auth login` once or
export the key only in the worker's shell. Then, from `services/agent-runtime`:

1. `python3 -m pip install -r requirements.txt`
2. One short run first:
   `set -a && source ../../.env.worker && set +a && FINMIND_RESEARCHER_TOKEN=... python3 scripts/smoke_claude.py`
   (S01, 3 virtual days, US$0.50 budget; it prints the cost and trace checks).
   A request is sent only if its worst case fits the remaining budget, from
   this repository's price table and Anthropic's token-count estimate plus a
   margin. That is not a billing limit: also set a spend limit in the
   Anthropic Console.
3. For experiments, create runs with `agent.provider` `anthropic`, a model ID
   (`claude-opus-5-5`, `claude-sonnet-5-5`, or `claude-haiku-5-5`),
   `agent.settings.effort`, and `maxCostMicroUsd`. Check current prices
   before larger batches; the table is in `apps/api/Finance/ProviderCatalogs.cs`.

## Checks

The PostgreSQL tests use a separate database, so they never touch your runs.
Create it once and migrate it like the main one:

```sh
docker compose --env-file .env -f infra/compose.yaml exec -T postgres createdb -U finmind finmind_test
for f in infra/db/migrations/*.sql; do
  docker compose --env-file .env -f infra/compose.yaml exec -T postgres \
    psql -v ON_ERROR_STOP=1 -U finmind -d finmind_test < "$f" || break
done
```

| Check | Command |
|---|---|
| Ledger and API tests | `set -a && source .env.test && set +a && dotnet test apps/api/Finance.Tests/Finmind.Finance.Tests.csproj` (PostgreSQL tests are skipped without `.env.test`) |
| Runtime tests | `python3 -m unittest discover -s services/agent-runtime/tests -v` (end-to-end tests need a running API and `FINMIND_E2E_API_URL`, `FINMIND_WORKER_TOKEN`, `FINMIND_RESEARCHER_TOKEN`) |
| Web tests and build | `cd apps/web && npm test && npm run build` |
| Crash drill (starts its own API on port 5080; stop yours first) | `dotnet build apps/api/Finmind.Api.csproj -c Release && set -a && source .env.test && set +a && python3 services/agent-runtime/scripts/crash_drill.py` |
| Phase 4 gate (API running, empty queue) | `services/agent-runtime/scripts/phase4_gate.py`; see `docs/reports/phase4-exit-gate.md` |
| Compose file | `docker compose --env-file .env -f infra/compose.yaml config --quiet` |

The values in the `.env*.example` files are placeholders for local
development. Never commit `.env` or any `.env.*` file other than the
examples.
