# FINMIND ADR 0013: Researcher Read API and Event Stream

- Status: Part A accepted (implemented; part B proposed); Part B proposed
- Date: 22 November 2026
- Scope: prerequisite corrections to Phase 2, then the read API and SSE stream for the Phase 3 UI

## Context

The UI needs wallets, ledger, actions, and a resumable event stream
(specification section 12). Reviewing the surface it would expose found
defects in the delivered Phase 2 code and documents. They change what a
researcher would see, or who could change it, so part A precedes part B.

## Part A: prerequisite corrections

Evidence: **R** reproduced on 22 November 2026, **C** found by reading code.

### A1. Before any paid run

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 1 | The cost cap is checked after the call. One turn (two 16,000-token attempts) records 648,000 against a 500,000 cap. The smoke script and README describe the cap as a maximum | R | Reserve the worst case before each request; lower `max_tokens` or stop; correct the wording |
| 2 | A paid call that ends after cancellation records no tokens or cost | R | Always record usage for the run, including after it is terminal |
| 3 | A model call may take 600 s with two retries; sessions last 10 minutes. The run can stay `RUNNING` with no valid session | C | Renew the session during calls; bound the call; reap expired sessions to `FAILED` |
| 4 | The worker crashes on a tool-call 413, an unreachable API at claim, or agent construction errors, leaving the run non-terminal | R | Contain per-run errors; return oversized calls to the agent as recorded malformed attempts |
| 5 | A retried `initialize` after a lost response fails a healthy run; a lost claim response strands it | R (initialize), C (claim) | Idempotent initialize; reclaim after session expiry |

#### A1 as implemented (migration 013)

- Reservations are `MODEL_COST_RESERVED` events plus
  `model_cost_reserved_micro_usd`. Cost plus reservations is what is compared
  with the cap. An unanswered attempt stays reserved.
- A usage report after the run stopped is accepted from a revoked session,
  only for a reserved turn.
- A `RUNNING` run with an expired session is failed every 30 seconds and at
  each claim. An `INITIALIZING` one is claimed again (`RUN_RECLAIMED`).
- Limits that remain: prices and token counts are estimates, no paid call
  has tested this path, the token budget is still checked after the call,
  and a 500 or timeout now fails the run instead of retrying.

### A2. Research integrity

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 6 | A resumed `wait` advances the clock twice (asked 3 days, moved 6). ADR-0009 says resume duplicates no effect | R | Bind the clock change to `toolCallId` in one transaction |
| 7 | Direct account, transfer, payment, and obligation endpoints change managed runs, contaminating agent results | R | Reject managed runs on these routes |
| 8 | Internal failures are recorded as `POLICY_DENIED`, contrary to ADR-0007, inflating denial metrics | R | Record `FINANCE_ERROR` |
| 9 | The event hash omits `trace_id`, `virtual_time`, `reason_code`, `outcome`, and the other promoted columns. Verification passes if they are altered | C | Hash scheme v2 over every stored field; keep v1 for old events |
| 10 | The 500 response always says ledger state did not change, which is false after a timeout following commit | R (message), C (timeout) | Say the outcome is unknown and to retry with the same key |
| 11 | Truncation can split a surrogate pair. PostgreSQL rejects the lone surrogate as JSONB, so an agent's text with an emoji at the limit makes recording fail | R (split and JSONB rejection), C (effect on the run) | Truncate on a code point boundary |

#### A2 as implemented (migration 014)

- A `wait` is bound to its `toolCallId` by the clock event written in the
  same transaction as the clock change.
- Direct routes refuse managed runs with `DIRECT_WRITE_NOT_ALLOWED`.
  Researcher reversal and lifecycle routes are unchanged.
- Hash v2 applies to new events. v1 events still verify and stay blind to
  their promoted columns.
- A failed write answers `committed: null`.

#### Found while implementing A2: approved 22 November 2026

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 16 | A tool call that committed but was not completed (interrupted, or still in flight) is recorded as `denied` with `committed: false` if a retry passes the session check and the run then stops | R | On resume, look up what the `toolCallId` committed before applying the run-status check, and return that |
| 17 | A lone surrogate sent by the agent (tool arguments, name, or output text) makes the API return 500 before the attempt is recorded, so the run fails | R | Replace invalid code units at the gateway and record the attempt as malformed |

#### Items 16 and 17 as implemented

- Item 16 was first reported too broadly. A response lost after completion
  was always recorded correctly, and a retry after cancellation gets 401.
  The false record needed the race now described in the table.
- A rejection is checked, under the run lock, against what that `toolCallId`
  committed. The recorded response is the one returned, and a finished call
  cannot commit later.
- A call interrupted before completion whose run then stops has no
  `TOOL_RETURNED`, because its retry gets 401. Part B must take "committed"
  from the ledger event, not from the tool-call outcome.
- Invalid Unicode becomes U+FFFD. A tool call holding it is recorded as
  malformed and never executed; a usage report is recorded with the
  replaced text.

#### Found while implementing items 16 and 17: approved 22 November 2026

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 18 | Other unreadable tool calls are not recorded: nesting over 64 levels or a non-finite number (400), a name of only control characters or a duplicate key (500). The first three can come through the worker and fail the run | R | One fallback at the gateway: record any tool call it cannot read as malformed, with its size and hash |

#### Item 18 as implemented

- The tool-call route records every authenticated body. One it cannot read,
  or could not store and read back (over 16 KB, deeper than 32 levels,
  numbers out of range, repeated names), is stored as
  `_unreadable: {bytes, sha256, problem}` and answered as malformed. The
  body itself is not kept. It counts as an action.
- Without a readable `toolCallId` the ID is `unreadable-` and the start of
  the hash, so a retry of the same body replays.
- Arguments the worker cannot serialise are sent as a malformed attempt.

#### Found while implementing item 18: approved 23 November 2026

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 19 | Items 17 and 18 record the attempt, but a Claude run cannot always continue. With a lone surrogate the SDK cannot send the history and the next turn fails as `UnicodeEncodeError`. With a non-finite number the SDK does not send the value it received, so history changes silently | R (offline SDK, mock transport); the real API's behavior is untested | After recording such a turn, end the run with the explicit reason `unrepresentable tool input`, without editing history, and report these runs separately |

Item 19 is implemented in the adapter and worker; the real API's handling of
such a response is still untested.

### A3. Trust boundary

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 12 | Researcher routes accept any `Host` and bodyless cross-origin POSTs. DNS rebinding could create and start runs, including paid ones | R | Host and Origin allowlists; researcher credential |
| 13 | The README worker command sources `.env`, giving the worker the database owner password, contrary to ADR-0009 and ADR-0011. The API receives the owner password the same way, weakening ADR-0004 | R | Separate environment files for owner/tests, API, and worker |
| 14 | Vite listens on `0.0.0.0`; an API proxy would expose researcher routes to the network | C | Bind to `127.0.0.1` |

#### A3 as implemented

- `RequestGuard` runs before routing. Every route needs `Host` to be
  `127.0.0.1:5080` or `localhost:5080`, and an `Origin`, if sent, from
  `FINMIND_ALLOWED_ORIGINS` (default: the Vite dev server). Researcher
  routes need the researcher token; worker routes and `/health` keep their
  own rules. Without a researcher token, researcher routes answer 401.
- The API refuses to start when its database role is privileged or owns
  tables, when the role cannot be checked, when an Anthropic credential is
  set, or when the two tokens are equal. The worker refuses database,
  researcher, and password-bearing settings.
- One environment file per process: `.env` (Compose and owner), `.env.api`,
  `.env.worker`, `.env.test`.
- Vite binds `127.0.0.1:5173` with a fixed port.

### A4. Incorrect evidence

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 15 | ADR-0011 cites end-to-end tests of parallel run isolation. No such test exists | R | Add the test; until then the Phase 2 gate is not fully evidenced |

Item 15: `tests/test_parallel_isolation.py` runs six mock runs on six
workers at once. It checks that no event, account, or posting crosses runs,
and that each run's trace equals the same configuration run alone. The test
passed on the existing code. The defect was the missing evidence, not a
behavior.

Each correction gets a regression test that fails before the fix.

## Part B: read API

All routes are under `/api/v1` and require the researcher credential.

| Method | Path | Returns |
|---|---|---|
| GET | `/runs` | Runs, newest first; filter by status and managed/legacy |
| GET | `/runs/{id}` | Snapshot, status, virtual day, usage against each budget |
| GET | `/runs/{id}/wallets` | Cash and savings from postings, with `asOfSequence` and reconciliation status |
| GET | `/runs/{id}/ledger` | Journals and postings with `effective` and reversal link |
| GET | `/runs/{id}/actions` | Tool calls: redacted arguments, decision, reason, journal, trace |
| GET | `/runs/{id}/events` | Ordered events after a sequence |
| GET | `/runs/{id}/stream` | SSE |
| GET | `/scenarios`, `/models` | Configured options; no credentials |

- Reads use a read-only `REPEATABLE READ` transaction and take no run lock,
  so observers never delay an agent.
- Cursors are event sequence numbers, not offsets. Default 100, maximum 500.
- Every displayed fact carries `evidence`: `proposed`, `denied`, `committed`,
  `reported_by_agent`, or `verified_by_ledger`. Gateway denials and ledger
  denials both map to `denied`, counted once per `toolCallId`.
- Unmanaged runs are marked `researchEligible: false`.

### Event stream

- Each message has `id` = sequence, `event` = type, `data` = the event.
- Resume with `Last-Event-ID` or `afterSequence`.
- The server polls once per second without holding a connection or
  transaction between polls. Sequence assignment holds the run row lock
  until commit (migration 007), so a cursor cannot skip a late commit.
- Heartbeat every 15 seconds. After a terminal status and the last event,
  send `end` and close.

### Researcher credential

- `FINMIND_RESEARCHER_TOKEN`, separate from the worker credential, at least
  32 characters, sent as a bearer header.
- The researcher enters it in the UI, which holds it in memory. It is never
  in the bundle, a URL, or browser storage. SSE uses `fetch` streaming
  because `EventSource` cannot send that header.
- This is a single-researcher local boundary. Accounts, roles, and run
  ownership need a later ADR before any shared deployment.

## Out of scope

UI screens, metrics and classification, export, replay, and multi-user
authentication.

## Acceptance tests

- A regression test for each item in part A.
- Missing or wrong credential, worker and session tokens, foreign Host, and
  foreign Origin are rejected on every researcher route.
- Wallets reconcile at `asOfSequence` during concurrent writes.
- Pagination and reconnect produce no gap or duplicate during concurrent
  writes.
- No credential appears in responses, streams, URLs, or logs.
