# FINMIND ADR 0014: Observatory UI (Phase 3)

- Status: Accepted 23 November 2026 (steps 1 to 3 implemented)
- Date: 23 November 2026
- Scope: the Phase 3 web screens, the API additions they need, and how the
  browser shows evidence

## Context

Phase 3 exit gate (plan section 3): a researcher can create, start, observe,
cancel, and inspect a run without database access. Labels distinguish
proposed, denied, committed, reported, and verified facts. An optional
pixel-art view must not misrepresent ledger state or hide evidence.

ADR-0013 delivered the read API, the event stream, and the researcher
credential. The web app is still the Phase 0 placeholder. The browser is a
read and control surface. It never decides a financial rule.

## Decision

### Screens (specification section 13, items 1 to 5)

| Screen | Content | Data |
|---|---|---|
| Runs | Runs newest first: status, scenario, model, research eligibility, action counts by evidence | `GET /runs`, `GET /runs/{id}` |
| New run | Objective, scenario, model, starting balance, transaction limit, duration, budgets, seed, then a review step | `GET /scenarios`, `GET /models`, create and start |
| Run (live) | Status and virtual day; cash and savings; budget use; action timeline; cancel | `GET /runs/{id}`, `/wallets`, `/actions`, the stream |
| Action detail | Arguments, the response the agent received, decision, reason, journal link, the turn's model text | `/actions`, `/events` |
| Ledger inspector | Journals and postings, effective and reversal links, reconciliation, chain verification | `/ledger`, `/wallets`, verify |

Behavior analytics, comparison, replay, and administration are Phase 4.

### Evidence in the browser

- Every action, balance, and agent statement shows one label as text, not
  color alone: PROPOSED, DENIED, COMMITTED, REPORTED BY AGENT,
  VERIFIED BY LEDGER. The label is the API's `evidence` value. The browser
  never derives one.
- Balances shown as authoritative come only from `/wallets`. A wallet that
  is not `reconciled` is shown as a warning, with both figures.
- Agent text (plans, final reports, model output) is shown as REPORTED BY
  AGENT, as quoted text. The browser never reads numbers out of it.
- The cancel button and the counts by evidence show their denominators
  (for example, "12 denied of 40 actions").
- Money is integer sen, formatted by integer arithmetic. No floating point.

### API additions (contract first, under `/api/v1`)

| Method | Path | Purpose |
|---|---|---|
| POST | `/runs` | Create a managed run (same validation as `POST /internal/runs`) |
| POST | `/runs/{id}/start` | Queue it |
| POST | `/runs/{id}/cancel` | Cancel, with a required reason |
| GET | `/runs/{id}/verify` | Event chain verification |

- Actions gain `response`: the stored response the agent received, already
  redacted. It is the agent's observation for the next turn.
- Experiment templates (`/experiments`) are deferred to Phase 4. Repeated
  runs reuse a run's configuration by copying it in the New run screen.
- The `/internal` researcher routes stay until the UI and tests no longer
  use them. Their removal is a later change.

### Browser boundary

- The researcher pastes the token into the app. It is held in memory only:
  not in storage, a cookie, a URL, or the bundle. Reloading the page asks
  for it again. A 401 clears it.
- The Vite dev server proxies `/api` to `127.0.0.1:5080`, so the browser
  sends its own Origin (`http://127.0.0.1:5173`), which the API allows.
- The stream uses `fetch` with the `Authorization` header and resumes with
  `Last-Event-ID` after a drop. It also re-reads wallets and actions after a
  reconnect, because an action can change after it was first shown.
- Agent and scenario text is rendered as text, never as HTML.
- Paid models are marked in the model list. Creating a paid run requires a
  cost budget and a confirmation that states the budget is an estimate, not
  a billing limit (ADR-0013 A1).
- Only the dev server is supported in Phase 3. Serving a production build
  needs its own decision (headers, CSP, origin) in Phase 5.

### Pixel-art view

Deferred to a separate ADR after these screens pass the exit gate. It
would be driven only by recorded events and sit beside the timeline, never
instead of it. The exit gate's pixel-art clause is then met by its absence.

### Dependencies and tests

- No new runtime dependency. Charts, if any, are plain SVG.
- `package.json` currently asks for `latest` versions; pin the versions the
  lockfile already resolved, so builds are reproducible.
- Pure logic (money formatting, the SSE parser, the API client, evidence
  label mapping) lives in plain TypeScript modules, compiled with the
  existing `tsc` and run with `node --test`. Node 20 cannot run TypeScript
  directly, and no test framework is added.
- Screens are checked by driving the real app in a browser against the
  test database, with mock agents only. Automated browser tests (for
  example Playwright) are a dependency decision left for Phase 5.

## Delivery

Each step ends with tests, a report, and a stop for review:

1. API additions and their tests.
2. Web foundation: token entry, API client, stream client, formatting,
   label component, pinned versions.
3. Runs and New run screens.
4. Run (live), action detail, and ledger inspector; browser check of the
   exit gate.

## Acceptance tests

- Every new route enforces the researcher credential, Host, and Origin.
- Create, start, cancel, and verify through `/api/v1` match `/internal`.
- The stream client resumes without gap or duplicate after a forced drop.
- Money formatting is exact for 0, negatives, and the largest allowed amount.
- The token never appears in storage, a URL, or a request other than the
  `Authorization` header.
- In the browser: a mock run can be created, started, watched, cancelled,
  and inspected; each evidence label appears; agent text is never shown as
  a verified figure.

## Out of scope

Analytics, comparison, replay, export, metrics, templates, multi-user
authentication, production hosting, and the pixel-art view.
