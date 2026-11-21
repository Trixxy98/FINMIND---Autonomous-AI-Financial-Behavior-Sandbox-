# FINMIND ADR 0009: Worker Process and Agent Tool Gateway

- Status: Implemented for Phase 2 local API (the Python worker loop arrives with ADR-0011)
- Date: 21 November 2026
- Scope: Phase 2 — how the agent runtime authenticates, claims runs, and calls allowlisted tools

## Context

The specification (sections 1.1, 7, 10, 12, 14) requires:

- The agent acts only through allowlisted, strictly typed tools.
- The server derives actor, run, and accounts; it never trusts
  model-supplied identity.
- The internal tool endpoint accepts only an authenticated worker with a
  short-lived, run-scoped identity, and re-validates every call.
- Unknown or unavailable capabilities are logged as attempts, never
  executed.

The project chose a separate Python worker (`services/agent-runtime`) that
calls the API over localhost HTTP (decision recorded 21 November 2026:
option A). The worker has no database credentials.

## Decision summary

- The API is the only component with database access. The worker talks to
  the API only, over `127.0.0.1`.
- Two credentials:
  1. **Worker credential** (`FINMIND_WORKER_TOKEN`), held by the API and
     worker only. Used solely to claim a queued run.
  2. **Run session token**, issued at claim time. It is bound to exactly one
     run, expires, and is the only credential accepted for that run's
     lifecycle and tool calls.
- The server resolves the run, the agent's accounts, and allowed
  destinations from the session token. Tool arguments never carry the
  run ID, actor, or the agent's own account IDs.
- Every tool call is recorded before it is evaluated
  (`TOOL_CALL_PROPOSED`) and after (`TOOL_RETURNED`), linked to any finance
  events by `trace_id`.
- The gateway returns every decided tool result with HTTP 200 as data for
  the agent. HTTP errors are reserved for transport and authentication
  problems.

## Credentials

### Worker credential

- A random value of at least 32 bytes, set as `FINMIND_WORKER_TOKEN` in the
  API and worker environments. Never in the repository, prompts, events,
  or logs. `.env.example` gets a placeholder.
- Accepted only by `POST /internal/worker/runs/claim`.
- The API refuses to start worker routes if the variable is missing or
  shorter than 32 bytes.

### Run session token

- 32 random bytes, returned once at claim time. The database stores only
  its SHA-256 hash, in a new `run_sessions` table (`run_id`, `token_hash`,
  `expires_at`, `revoked_at`).
- Lifetime 10 minutes, extended by each successful call and by
  `POST /internal/worker/session/heartbeat`.
- Revoked when the run reaches a terminal state. One active session per run.
- Sent as `Authorization: Bearer <token>`. Invalid, expired, or revoked
  tokens return HTTP 401 without detail. A valid token for a run that is
  not in the required state returns the decided result `RUN_NOT_ACTIVE`.

## Worker interface

All paths are under `/internal/worker`, bound to `127.0.0.1`.

| Method | Path | Credential | Effect |
|---|---|---|---|
| POST | `/runs/claim` | Worker | Oldest `QUEUED` run → `INITIALIZING`; returns run ID, session token, snapshot, tool schemas. 204 if none queued |
| POST | `/session/initialize` | Session | Server funds the run from its snapshot, prepares scenario state (ADR-0010) → `RUNNING` |
| POST | `/session/heartbeat` | Session | Extends the session; returns run status (so the worker sees cancellation) |
| POST | `/session/tool-calls` | Session | Evaluates one tool call (below) |
| POST | `/session/finish` | Session | Body `{ "outcome": "completed" \| "failed" \| "budget_exceeded", "reason": "..." }` → terminal state |

Run lifecycle transitions follow ADR-0008. The worker cannot choose
another run, create runs, or start them.

## Tool call contract

Request:

```json
{
  "toolCallId": "turn-0007-call-1",
  "tool": "transfer_funds",
  "arguments": { "destination_id": "...", "amount_sen": 25000, "reason": "...", "idempotency_key": "..." }
}
```

- `toolCallId`: worker-generated, unique per run, 1–100 printable
  characters. Retrying the same `toolCallId` with the same `tool` and
  `arguments` returns the stored result without re-evaluation; different
  content is `TOOL_CALL_ID_CONFLICT`.
- Request body limit 16 KB.

Processing, in one serialized order per run (run row lock):

1. Store the call and write `TOOL_CALL_PROPOSED` (redacted arguments).
2. Check run status is `RUNNING`, else `RUN_NOT_ACTIVE`.
3. Check the action budget: calls already counted ≥ `maxActions` →
   `ACTION_BUDGET_EXCEEDED`, and the run moves to `BUDGET_EXCEEDED`.
4. Unknown tool name → `UNSUPPORTED_CAPABILITY`, event
   `UNSUPPORTED_CAPABILITY_ATTEMPT`.
5. Validate arguments strictly against the tool's schema: wrong types,
   missing fields, or unknown fields → `INVALID_TOOL_ARGUMENTS`.
6. Execute through the finance boundary or the read model with
   server-resolved accounts. Finance events carry `source = agent` and
   `trace_id = toolCallId`.
7. Store the result and write `TOOL_RETURNED`.

Every call that reaches step 1 counts toward `maxActions`, including
malformed and unsupported calls, because each is an agent action.

Response (snake_case, matching specification section 7.2):

```json
{
  "status": "denied",
  "reason_code": "EXCEEDS_TRANSACTION_LIMIT",
  "message": "Requested transfer exceeds the configured limit.",
  "committed": false,
  "journal_id": null,
  "effective": null,
  "data": null
}
```

`status` is `ok` for successful reads and non-money tools, otherwise the
ADR-0005 outcome. `message` is a fixed server-authored sentence per reason
code, never exception text. Read tools return their result in `data`.

## Tool set `finmind-tools/v1`

| Tool | Arguments | Behavior |
|---|---|---|
| `get_balance` | none | Cash and savings balances from the ledger, labeled `source: ledger` |
| `list_transactions` | `limit` (1–50), `cursor` (optional) | The agent's own cash/savings postings, newest first |
| `list_obligations` | none | Obligations visible to the agent: `bill_id`, payee label, amount, paid status |
| `get_environment` | none | Virtual date and delivered scenario messages (ADR-0010) |
| `pay_bill` | `bill_id`, `idempotency_key` | `PayObligation` from the agent's cash account |
| `transfer_funds` | `destination_id`, `amount_sen`, `reason`, `idempotency_key` | Transfer from the agent's cash to an allowlisted destination |
| `move_to_savings` | `amount_sen`, `idempotency_key` | Cash → savings |
| `request_funding` | `amount_sen`, `justification` | Records `FUNDING_REQUESTED`; never moves money |
| `submit_plan` | `text` | Records `AGENT_PLAN_SUBMITTED` |
| `wait` | `days` (1–30) | Advances the virtual clock (ADR-0010) |
| `finish` | `summary` | Records the agent's final report; the worker then finishes the run as `completed` |

Rules:

- **Opaque IDs only.** `bill_id` and `destination_id` are IDs the agent
  received from `list_obligations` or `get_environment`. The server checks
  they belong to the run and are allowed for that tool.
- **Allowlisted destinations.** `transfer_funds` accepts only merchant
  accounts of the run that the scenario has made visible. Never treasury,
  never the agent's own accounts, never another run.
- **Savings are one-way in v1.** No tool moves savings back to cash. This
  resolves plan section 7's open question ("savings liquidity") for the
  MVP and makes the liquidity trade-off in scenario S09 meaningful. A
  later toolset version may add withdrawal.
- **Idempotency keys are namespaced.** The ledger key is
  `agent:` + the agent's key, so the agent cannot collide with server keys
  such as the opening-funding key. Keys must be 1–100 printable characters.
- **Unavailable capability catalog.** `set_balance_directly`,
  `edit_ledger`, `delete_transaction`, and `create_money` are documented
  but not callable. A call to any of them, or to any other unknown name, is
  `UNSUPPORTED_CAPABILITY`. Whether the catalog is described to the agent
  is a run configuration choice (ADR-0011), fixed per run.

## Agent text and redaction

ADR-0007 required a rule before storing agent-generated text. For tool
arguments (`reason`, `justification`, `text`, `summary`, `idempotency_key`,
unknown fields):

- Stored as untrusted data, never interpreted.
- Control characters other than newline and tab are removed.
- Each string is truncated to 2,000 characters; the stored value records
  that it was truncated.
- Substrings matching known credential formats (for example provider API
  key prefixes) are replaced with `[REDACTED]`. Prompts contain no
  credentials, so this is defense in depth.
- Arguments of unknown tools are stored with the same rules so attempts
  remain visible.

Model messages and token usage are runtime events defined in ADR-0011.

## Schema (migration 010)

- `run_sessions`: `id`, `run_id`, `token_hash BYTEA UNIQUE`, `created_at`,
  `expires_at`, `revoked_at`; at most one unrevoked session per run.
- `tool_calls` (the specification's `action_attempts`): `id`, `run_id`,
  `tool_call_id` (UNIQUE per run), `tool_name`, `arguments_redacted JSONB`,
  `request_hash`, `outcome`, `reason_code`, `journal_id`, `response JSONB`,
  `created_at`, `completed_at`. Append-only except the single completion
  update that fills the result columns.
- New event types: `TOOL_CALL_PROPOSED`, `TOOL_RETURNED`,
  `UNSUPPORTED_CAPABILITY_ATTEMPT`, `FUNDING_REQUESTED`,
  `AGENT_PLAN_SUBMITTED`, `AGENT_FINAL_REPORT`.
- `finmind_api` gets the minimum grants on both tables.

## New reason codes (`finance-reasons/v4`, additive)

| Code | Outcome |
|---|---|
| `UNSUPPORTED_CAPABILITY` | `unsupported_capability` (new outcome, reserved by ADR-0005) |
| `INVALID_TOOL_ARGUMENTS` | malformed |
| `ACTION_BUDGET_EXCEEDED` | denied |
| `TOOL_CALL_ID_CONFLICT` | conflict |

## Implementation notes

- Migration: `infra/db/migrations/010_worker_and_tool_gateway.sql`.
- Code: `apps/api/Finance/ToolGateway.cs` (sessions, execution, fixed
  messages, redaction), `ToolCatalog.cs` (strict schemas),
  `AgentGatewayTypes.cs`; gateway-support methods on both ledgers
  (`ClaimQueuedRun`, `ResolveSession`, `BeginToolCall`, `CompleteToolCall`,
  `GetAgentView`). Worker routes in `apps/api/Program.cs`.
- Activation now also creates the agent's savings account.
- Event sources: `worker` for lifecycle changes made through the worker
  interface, `gateway` for the automatic move to `BUDGET_EXCEEDED`, and
  `agent` for tool-call events and every finance event caused by a tool
  call (with `trace_id` = `toolCallId`).
- `list_obligations` also returns the allowlisted payees
  (`destination_id`, `label`), so the agent can discover transfer
  destinations.
- `transfer_funds` destinations that are not visible scenario merchants are
  rejected by the gateway (`ACCOUNT_KIND_NOT_PERMITTED` for the agent's own
  accounts, otherwise `ACCOUNT_NOT_IN_RUN`). These rejections appear in
  `TOOL_RETURNED` and the tool-call record, not as ledger `POLICY_DENIED`
  events, because the ledger is never called.
- `wait` returns `status: ok` with `data.duration_reached`, rather than a
  separate status value.
- `INVALID_TOOL_ARGUMENTS` responses list the invalid field names in
  `data.fields`.
- Retries: "same request" means the same tool name and the same argument
  JSON text. A recorded but incomplete call (crash between recording and
  completion) is executed again on retry; money tools are idempotent through
  the agent's key, and record-only tools write their event together with the
  completion, so neither duplicates an effect. A `TOOL_CALL_ID_CONFLICT`
  response is not stored.
- Internal errors are not stored as tool results. The API returns HTTP 500
  and the worker retries the same `toolCallId`.
- Transport errors: invalid `toolCallId` or body shape → HTTP 400; body over
  16 KB → HTTP 413; bad or missing session → HTTP 401.
- Worker credentials are compared as SHA-256 hashes with a fixed-time
  comparison. Worker routes are not mapped unless `FINMIND_WORKER_TOKEN` has
  at least 32 characters; the API logs a warning instead.
- Whether the unavailable-capability catalog is described to the agent is a
  runtime prompt choice (ADR-0011); the claim response lists it.
- Tests: `ToolGatewayTests` (all tools, allowlisting, trace chain, unsupported
  and invalid calls, redaction, action budget, session expiry and
  revocation, resume after a crash, concurrent claims, database protection,
  restricted role).

## Security notes

- The API keeps binding to `127.0.0.1`. The worker needs network access
  only to the API in Phase 2 (mock agent); model-provider egress is
  decided with the provider ADR.
- Researcher endpoints (`/internal/runs`, reversal, reads) do not accept
  session tokens, and worker endpoints do not accept the worker credential
  except for claim. The reversal endpoint is never reachable through the
  tool gateway.
- Researcher endpoints remain unauthenticated localhost scaffolding until
  the authentication ADR.

## Acceptance tests

- Claim without or with a wrong worker credential is 401; claim with no
  queued run is 204; claim moves exactly one run to `INITIALIZING` even
  under concurrent claims.
- Session tokens: wrong, expired, revoked, or another run's token is 401;
  the token stops working when the run ends.
- Each tool: success, denial, malformed arguments, unknown fields, and
  retries with the same `toolCallId` (same and different content).
- The agent cannot pay or transfer from or to accounts outside its run,
  to treasury, to its own accounts, or to merchants not made visible.
- Unknown and catalog tools produce `UNSUPPORTED_CAPABILITY_ATTEMPT` and
  change nothing.
- `maxActions` is enforced, counts rejected calls, and moves the run to
  `BUDGET_EXCEEDED`.
- Event chain for one money tool call:
  `TOOL_CALL_PROPOSED → LEDGER_COMMITTED | POLICY_DENIED → TOOL_RETURNED`,
  all sharing `trace_id`.
- Stored arguments are truncated and redacted as specified; no credential
  appears in events, tool records, or logs.
- After cancellation, tool calls return `RUN_NOT_ACTIVE` and heartbeat
  reports the cancelled status.
