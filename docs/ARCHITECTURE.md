# FINMIND architecture

FINMIND is a local research sandbox. An AI agent manages virtual Malaysian
ringgit (MYR, stored as integer sen) through a small set of typed tools, and
everything it proposes, and everything that happens, is recorded. No real
money, bank, payment service, or real destination is connected.

Decisions behind each part are in `docs/decisions/` (ADR-0001 to ADR-0017).
Threats and the tests behind each control are in
`docs/security/threat-model.md`.

## Components

```
 Browser (React, 127.0.0.1:5173)            Worker (Python, one process per worker)
   researcher token in memory                 worker token; provider key for Claude runs
            │ /api/v1 (bearer)                          │ /internal/worker (bearer, then session)
            ▼                                           ▼
 ┌──────────────────────────────────────────────────────────────────────┐
 │ API (.NET 10, 127.0.0.1:5080)                                         │
 │  RequestGuard: host, origin, token        ToolGateway: 11 tools,      │
 │  Research reads, metrics, comparison,     strict schemas, run-scoped  │
 │  classification, export, operator stop    sessions, budgets           │
 │                     PostgresLedger: policy, journals, events          │
 └──────────────────────────────┬───────────────────────────────────────┘
                                │ restricted role finmind_api
                                ▼
                   PostgreSQL 18 (Docker, 127.0.0.1:5432)
          triggers and constraints enforce what the code also checks
```

| Part | Location | Holds |
|---|---|---|
| API | `apps/api` | Ledger, policy, tool gateway, research API |
| Worker | `services/agent-runtime` | Agent loop, mock agents, Claude adapter |
| Web app | `apps/web` | Run list, run detail, comparison, review, replay, scene |
| Database | `infra/db/migrations` | Schema, triggers, grants (applied as the owner role) |
| Scenarios | `scenarios` | Versioned synthetic scenario files |
| Prompt | `prompts` | Versioned agent prompt for provider runs |

## Trust boundaries

1. **Model and tool arguments → tool gateway.** Untrusted. The gateway
   resolves the run and the agent's accounts from the session token, never
   from arguments. Unknown tools return `unsupported_capability`; invalid
   arguments are recorded as malformed. Nothing a model says changes policy.
2. **Worker → API.** The worker token can only claim runs. Each claim returns
   a short-lived session token that works only for that run's tools. The
   worker never holds database or researcher credentials (it refuses to start
   with them).
3. **Browser → API.** Only `127.0.0.1:5080` or `localhost:5080` as host,
   only configured origins, and a researcher bearer token kept in browser
   memory (no cookie).
4. **API → database.** The API connects as `finmind_api`, which can read and
   insert but cannot change or delete protected history. The API refuses to
   start as the owner or any privileged role.
5. **Provider credentials.** Only the worker has them. The API refuses to
   start if an Anthropic credential is in its environment.

## A run, step by step

1. A researcher creates a run. The configuration is validated, frozen as a
   snapshot with a hash, and stored with every version string (scenario and
   hash, prompt, tools, policy, reason catalog, pricing, code revision).
2. Start moves it to `QUEUED`. A worker claims it (`INITIALIZING`) and
   receives a session token.
3. Activation (`RUNNING`) funds the cash account from the snapshot only and
   materializes the scenario: bills, messages, and the virtual clock.
4. Each turn: the worker sends a heartbeat, asks the agent for a decision,
   records the model invocation (tokens, cost), and sends each tool call.
5. The gateway records the call as proposed, evaluates it, commits a balanced
   journal if allowed, and records what it returned. Denied calls change
   nothing.
6. The run ends by `finish`, a budget (actions, tokens, cost, wall time,
   virtual days), cancellation, the operator stop, or failure.

Statuses: `DRAFT`, `QUEUED`, `INITIALIZING`, `RUNNING`, then one of
`COMPLETED`, `FAILED`, `CANCELLED`, `BUDGET_EXCEEDED`.

## The tools (`finmind-tools/v1`)

Read: `get_balance`, `list_transactions`, `list_obligations`,
`get_environment`. Money: `pay_bill`, `transfer_funds`, `move_to_savings`
(each needs an idempotency key). Other: `request_funding` (recorded, never
credited), `submit_plan`, `wait`, `finish`.

Capabilities a model may try but that do not exist: `set_balance_directly`,
`edit_ledger`, `delete_transaction`, `create_money`.

## Where each invariant is enforced

| Invariant | In code | In the database |
|---|---|---|
| Money is integer sen, positive, within limits | Ledger validation, tool schemas | Column types and checks |
| Every journal balances | Ledger builds postings in pairs | Deferred trigger: two distinct postings summing to zero |
| History is never edited | No update or delete paths | Append-only triggers; `finmind_api` grants |
| Corrections are reversals | A reversal is a new journal with the opposite postings | Reversal checks (matching postings, one reversal) |
| One economic effect per idempotency key | Replay on repeated key, conflict on a different payload | Unique `(run_id, idempotency_key)` |
| Run isolation | Accounts resolved from the session | Postings reference `(account, run, currency)` and `(journal, run, currency)` |
| Policy cannot change during a run | Snapshot is the only source | Snapshot and policy immutability triggers |
| Ledger is the source of balances | Wallets are projections, reconciled with postings | Postings are the only balance data |
| Records are tamper-evident | Hash chain v1/v2, verification | Sequence and hash assigned in the database |

## Records and versions

- Events (`run_events`): append-only, one hash chain per run. Types include
  `RUN_STATUS_CHANGED`, `TOOL_CALL_PROPOSED`, `POLICY_DENIED`,
  `LEDGER_COMMITTED`, `TOOL_RETURNED`, `MODEL_INVOCATION_COMPLETED`,
  `SCENARIO_EVENT_DELIVERED`, and `CLOCK_ADVANCED`.
- Evidence labels on every action: `committed`, `denied`,
  `reported_by_agent`, `proposed`; balances are `verified_by_ledger`.
- Versioned definitions: `finmind-metrics/v1`, `finmind-compare/v2`,
  `finmind-classifications/v1`, `finmind-outcomes/v1`, `finmind-export/v2`, `finance-reasons/v8`,
  `finmind-cues/v1`.
- Other append-only tables: `run_batches`, `classification_reviews`,
  `operator_switch_log`, `export_log`.

## Operating it

- The operator stop (`POST /api/v1/admin/stop`, or "Stop all runs" in the
  app) cancels every active run and refuses starts until resumed.
- A worker that dies leaves its run `RUNNING` until the session expires
  (10 minutes by default), then the run is `FAILED`. A run claimed but not
  yet started is offered again.
- `scripts/crash_drill.py` in `services/agent-runtime` exercises these with
  real processes; CI runs it.
