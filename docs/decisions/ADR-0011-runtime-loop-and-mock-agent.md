# FINMIND ADR 0011: Runtime Loop, Deterministic Mock Agent, and Budgets

- Status: Implemented (Phase 2 exit gate evidence in CI)
- Date: 21 November 2026
- Scope: Phase 2 — the Python worker loop, provider-neutral agent interface, deterministic mock agents, and wall-time and token budgets

## Context

ADR-0009 defined the worker interface and tool gateway; ADR-0010 the
scenarios and clock. Nothing yet drives a run. The Phase 2 exit gate
(plan section 3) is: *an end-to-end mock run emits an inspectable causal
trace; run isolation, cancellation, and limits are tested.* The plan
requires the deterministic mock path before any provider integration, and
CI must not call paid providers.

## Decision summary

- `services/agent-runtime` becomes a Python package `finmind_runtime` using
  the standard library only (no new dependencies). It talks to the API over
  HTTP and has no database credentials.
- A provider-neutral `Agent` interface separates "decide the next action"
  from the loop. Phase 2 implements only mock agents; real providers come
  in a later ADR behind the same interface.
- Mock agents are deterministic: same run configuration and scenario give
  the same decisions, tool call IDs, and idempotency keys.
- Budgets are enforced on the server, with the worker stopping early as a
  courtesy: actions (ADR-0009), wall time (new), and model tokens (new).
- Every turn is recorded as a model invocation event before its tool call,
  so the trace shows what the agent decided and what happened.

## Worker loop

```
claim (worker credential)          → run + session token, or nothing to do
initialize (session)               → RUNNING, day 0
repeat:
  heartbeat                        → stop if status is not RUNNING
  observation = previous tool result + turn number
  decision = agent.next_action(observation)
  report model invocation          → MODEL_INVOCATION_COMPLETED (tokens, latency, redacted output)
  call tool (toolCallId = "turn-NNNN")
  stop when: agent called finish, wait reports duration_reached,
             a budget response arrives, or the session ends (401)
finish (session)                   → COMPLETED | FAILED | BUDGET_EXCEEDED
```

- Command: `python3 -m finmind_runtime.worker` (poll every 2 seconds) or
  `--once` (claim one run, run it, exit; used by tests and CI).
- Configuration: `FINMIND_API_URL` (default `http://127.0.0.1:5080`) and
  `FINMIND_WORKER_TOKEN`. Tokens are never logged.
- Finish outcome: `completed` after `finish` or the end of the virtual
  duration; `budget_exceeded` when a budget stops the run (the server may
  already have moved it); `failed` after unrecoverable errors.
- If the run is no longer `RUNNING` (cancelled, or ended by the server), the
  worker stops without calling finish.

### Retries and timeouts

- HTTP timeout 10 seconds per request.
- Transport errors, timeouts, and HTTP 5xx: retry the **same** request
  (same `toolCallId`) up to 3 times with 0.5 s, 1 s, 2 s backoff. The gateway
  makes this safe: a completed call is replayed, an interrupted one resumes,
  and money tools are idempotent (ADR-0009).
- After the retries fail, the worker finishes the run as `failed` with the
  reason `runtime: API unreachable`.
- HTTP 401 during a session means the session ended; the worker stops.
- Amended by ADR-0013 A1: the session is renewed during a model call; an
  error inside one run fails that run and not the worker; tool calls over
  the size limit are sent as recorded malformed attempts.

### Cancellation bound

The worker checks status before every turn. After cancellation, at most one
in-flight tool call completes; it is rejected with `RUN_NOT_ACTIVE` by the
server anyway (ADR-0008). Cancellation therefore takes effect at the ledger
immediately and at the worker within one turn.

## Agent interface

```python
class Agent(Protocol):
    def next_action(self, observation: Observation) -> Decision: ...

Decision = (tool: str, arguments: dict, output_text: str,
            input_tokens: int, output_tokens: int)
```

`Observation` contains the objective, the tool schemas from the claim
response, the turn number, and the last tool response. The agent never
receives the session token, run ID, or account IDs other than those
returned by tools.

## Mock agents

Selected by `agent.modelId` (provider `mock`). Added to the server's model
catalog:

| Model ID | Behavior | Purpose |
|---|---|---|
| `finmind-mock-v1` | Prudent household: reads environment and bills, pays each bill once it is due within 3 days, moves 10% of starting cash to savings once, waits, submits a plan on turn 1, finishes with a factual report at the end | Normal end-to-end trace |
| `finmind-mock-adversarial-v1` | Tries unavailable capabilities, over-limit and self transfers, malformed arguments, a duplicate payment with a new key, a funding request, then behaves prudently | Every denial and attempt path |
| `finmind-mock-chaotic-v1` | Seeded random tool calls and arguments (valid and invalid), using the run seed and the ADR-0010 PRNG | Fuzzing the gateway end to end |

Determinism rules:

- Decisions depend only on observations, the turn number, and the run seed.
- `toolCallId` is `turn-<4-digit turn>`; idempotency keys are derived from
  the turn and the bill or action, never random.
- Mock agents report 0 tokens and a fixed latency of 0 ms. Their
  `output_text` is a short fixed sentence describing the decision.
- The chaotic mock uses `prng/xoshiro256starstar-v1` seeded from
  `SHA-256("finmind-mock-chaotic-v1|" + seed)`, implemented in Python with the
  same test vector style as ADR-0010.

## Model invocation events

New worker endpoint: `POST /internal/worker/session/model-invocations` with
`{ "turn", "inputTokens", "outputTokens", "latencyMs", "outputText" }`.

- Records `MODEL_INVOCATION_COMPLETED` (source `agent`, `trace_id` =
  `turn-NNNN`, the same as the turn's tool call). `outputText` follows the
  ADR-0009 redaction rule.
- The server adds the tokens to the run's total. If the total would exceed
  `maxModelTokens`, it records the event, moves the run to
  `BUDGET_EXCEEDED` (source `gateway`), revokes the session, and returns
  `TOKEN_BUDGET_EXCEEDED`.
- For mock agents the counts are 0, so the token budget never triggers;
  tests exercise it by reporting tokens directly.

## Wall-time budget

The server measures wall time from `started_at` (entering `RUNNING`). Before
evaluating any tool call or model invocation, if now ≥ `started_at` +
`maxWallTimeSeconds`, it records the attempt, moves the run to
`BUDGET_EXCEEDED`, revokes the session, and returns
`WALL_TIME_BUDGET_EXCEEDED`. The worker also stops itself when its own clock
passes the budget.

## New reason codes (`finance-reasons/v5`, additive)

| Code | Outcome |
|---|---|
| `WALL_TIME_BUDGET_EXCEEDED` | denied |
| `TOKEN_BUDGET_EXCEEDED` | denied |

## Schema (migration 011)

- `run_events` event types: add `MODEL_INVOCATION_COMPLETED`.
- `experiment_runs`: `model_tokens_used BIGINT NOT NULL DEFAULT 0`
  (only increases; `finmind_api` gets UPDATE on this column).

## Testing and CI

- Python unit tests (no network): each mock agent's decisions for scripted
  observations; determinism; chaotic PRNG test vector; retry logic with a
  fake transport that drops responses.
- C# tests: model-invocation endpoint logic, token and wall-time budgets on
  both ledgers.
- End-to-end test `services/agent-runtime/tests/test_end_to_end.py`, skipped
  unless `FINMIND_E2E_API_URL` is set. CI starts the API against the test
  database with a generated worker token, then:
  - runs each mock on S01, S02, and S03 to a terminal state;
  - checks the causal chain for every tool call
    (`MODEL_INVOCATION_COMPLETED → TOOL_CALL_PROPOSED → … → TOOL_RETURNED`),
    ledger reconciliation, and the event hash chain;
  - repeats one configuration and compares the two traces (event types,
    outcomes, reason codes, amounts) for determinism;
  - cancels a run mid-way and checks no money moves after cancellation;
  - runs with `maxActions` 5 and checks `BUDGET_EXCEEDED`;
  - kills the response of one money call (fault-injecting transport) and
    checks the retry produced exactly one journal.

## Phase 2 exit gate mapping

| Gate requirement | Evidence |
|---|---|
| End-to-end mock run with inspectable causal trace | E2E tests on S01–S03 with chain checks |
| Run isolation | Existing cross-run tests; E2E runs in parallel runs never share events or accounts |
| Cancellation | E2E cancel test plus ADR-0008 race test |
| Limits | Actions (ADR-0009), wall time and tokens (this ADR), transaction cap (ADR-0002) |
| Reproducibility | Determinism comparison of repeated runs |

## Implementation notes

- Python: `services/agent-runtime/finmind_runtime/` (`client.py`, `agents.py`,
  `worker.py`, `prng.py`); unit tests and `tests/test_end_to_end.py`.
- Server: migration `011_model_invocations_and_budgets.sql`;
  `RecordModelInvocation` and wall-time checks on both ledgers;
  `ToolGateway.RecordModelInvocation`; endpoint
  `POST /internal/worker/session/model-invocations`. `InMemoryLedger` takes
  a `TimeProvider`; `PostgresLedger` measures wall time with database time.
- A retried invocation for the same turn is answered as `replayed` and its
  tokens are not counted twice.
- The worker's own action cap is `maxActions + 1`, only as a safety net, so
  the server's `ACTION_BUDGET_EXCEEDED` path is what ends runs (and is what
  the end-to-end test checks). The worker's wall-time check stays a
  courtesy stop.
- Worker results include `stopped` (the server ended the run, or it was
  cancelled); the worker then does not call finish.
- The prudent mock pays a bill when it is due within 3 days of the current
  virtual day.
- New researcher endpoint `GET /internal/runs/{runId}/events/verify` runs
  `finmind_verify_run_events`.
- CI starts the API from the Release build as `finmind_api` with a generated,
  masked worker token, then runs the Python unit and end-to-end tests.
- Tests: `BudgetTests` (C#, both ledgers), 18 Python unit tests, 5 Python
  end-to-end tests (all mocks × S01–S03, determinism, cancellation, action
  budget, dropped payment response).

## Out of scope

- Real model providers, prompt templates, memory modes, cost metering in
  currency, and egress rules: next ADR.
- Describing the unavailable-capability catalog to the agent: a run
  configuration option added with the provider ADR.
- Scenario S11 (payment timeout as a scenario event): Phase 4. This ADR
  tests transport retries only.
