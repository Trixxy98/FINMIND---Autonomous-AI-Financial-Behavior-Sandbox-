# FINMIND ADR 0013: Researcher Read API and Event Stream

- Status: Part A accepted (A1 implemented; A2 to A4 pending); Part B proposed
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

### A3. Trust boundary

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 12 | Researcher routes accept any `Host` and bodyless cross-origin POSTs. DNS rebinding could create and start runs, including paid ones | R | Host and Origin allowlists; researcher credential |
| 13 | The README worker command sources `.env`, giving the worker the database owner password, contrary to ADR-0009 and ADR-0011. The API receives the owner password the same way, weakening ADR-0004 | R | Separate environment files for owner/tests, API, and worker |
| 14 | Vite listens on `0.0.0.0`; an API proxy would expose researcher routes to the network | C | Bind to `127.0.0.1` |

### A4. Incorrect evidence

| # | Defect | Evidence | Correction |
|---|---|---|---|
| 15 | ADR-0011 cites end-to-end tests of parallel run isolation. No such test exists | R | Add the test; until then the Phase 2 gate is not fully evidenced |

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
