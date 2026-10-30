# FINMIND Implementation Plan

Based on `FINMIND_Full_System_Documentation.md` v1.0 (29 October 2026)

## 1. Product understanding

FINMIND is a local-first research sandbox for measuring how AI agents manage virtual MYR under controlled scenarios. It is not a banking, investment, payment, or financial-advice product. The agent proposes actions through typed tools; trusted server-side policy and finance components decide whether those actions commit. The ledger—not model text, UI state, or a wallet cache—is the source of truth.

Research must distinguish proposals, denied attempts, committed changes, and agent-reported claims. Findings are scoped to the recorded model, prompt, tools, policy, scenario, seed, and run; they do not establish intent or universal model traits.

## 2. Non-negotiable safety and accounting rules

1. Virtual currency and fictitious entities only; no real-money integrations or destinations.
2. No agent access to shell, SQL, filesystem, Docker socket, arbitrary code execution, raw DB credentials, or unrestricted network.
3. Only allowlisted typed capabilities; server derives actor/run identity and validates every call.
4. Financial changes occur only as atomic, balanced, immutable double-entry journals.
5. Use integer sen for monetary calculations and transport; reject invalid, overflowed, non-positive, wrong-currency, over-limit, unauthorized, or cross-run requests.
6. Denied/failed calls never change balances. Idempotency prevents duplicate economic effects on retries.
7. Append-only action/event history records proposals, decisions, commits, outcomes, and relevant claims.
8. Separate agent-reported figures from ledger-verified figures in storage, API, UI, exports, and reports.
9. Bound model tokens, tool calls, simulated duration, wall time, and compute; support cancellation and safe recovery.
10. Synthetic scenario text is untrusted data and cannot grant authority or modify policy.

## 3. Delivery strategy and gates

### Phase 0 — Foundations

Deliver a documented monorepo skeleton, local PostgreSQL Compose setup, health endpoints, CI, environment example, and deterministic test entry points. Do not implement financial mutations yet.

Exit gate: clean checkout builds and starts using documented commands; schema/config validation and baseline tests pass.

### Phase 1 — Trusted money core

Implement domain types, account/run scoping, opening funding, journals/postings, bill payment, transfer, savings, balance reads, idempotency, transaction limits, and persistence constraints. Add unit, property/invariant, and concurrency tests.

Exit gate: every committed journal balances; denied operations leave state unchanged; retries have at most one economic effect; projections reconcile to postings.

### Phase 2 — Experiment and agent runtime

Add immutable run configuration snapshots, lifecycle, seeded scenario loading, virtual clock, tool schemas, event sequence, budget enforcement, and a deterministic mock agent. Provider integrations come only after the mock path is reproducible.

Exit gate: end-to-end mock run emits an inspectable causal trace; run isolation, cancellation, and limits are tested.

### Phase 3 — Observatory UI

Add experiment setup, run control, live event stream, verified wallet view, action detail, policy verdicts, and ledger inspector. Use an optional pixel-art simulation layer to make run behavior approachable: visualize agent activity from recorded events while retaining precise timelines and ledger details. The browser is a read/control surface, never the authority for financial rules.

Exit gate: researcher can create, start, observe, cancel, and inspect a run without direct DB access; labels distinguish proposed/denied/committed/reported/verified, and the pixel-art view cannot misrepresent ledger state or hide the underlying evidence.

### Phase 4 — Research workflows

Add S01, S02, S03 first; then remaining scenarios, versioned metrics/classifications, matched comparisons, replay, and JSONL/CSV export. Human review handles ambiguous behavioral labels.

Exit gate: at least three seeded replications of two scenarios are comparable, reproducible, and accompanied by limitations and denominators.

### Phase 5 — Hardening and publication

Review threat model, access controls, isolation, concurrency and crash recovery; add documentation, synthetic sample data, report template, and reproducible demo.

Exit gate: independent developer can reproduce mock results locally; no real financial integration exists; all critical integrity tests pass.

## 4. Suggested work breakdown

- Domain: MoneySen, Currency, RunId, AccountId, Journal, Posting, policy decision.
- Persistence: migrations, scoped foreign keys, unique `(run_id, idempotency_key)`, append-only roles, concurrency control.
- Contracts: OpenAPI, strict tool schemas, event taxonomy, versioned export format.
- Runtime: provider-neutral model interface, finite-state loop, deterministic mock, redaction, budgets.
- Provider selection: list only configured provider/model adapters (e.g. Anthropic/Claude, OpenAI/GPT, Google/Gemini, Ollama/local); record exact model and experiment metadata. No claim of universal agent compatibility.
- Simulator: seeded fixtures, virtual time, synthetic obligations and events.
- API: authentication/RBAC, run orchestration, read models, cancellation, SSE and export.
- Web: wizard with configured model/provider selection, pixel-art observatory driven by run events, run detail, ledger reconciliation, analytics and comparison. Keep authoritative balances and evidence inspectable outside the game-like presentation.
- Quality: unit/property/integration/E2E tests, threat fixtures, reproducibility checks, operational docs.

## 5. Recommended initial implementation order

1. Inspect repository and record existing conventions before scaffolding.
2. Write domain decisions and API/event contracts before parallel implementation.
3. Build ledger and invariant tests before connecting any LLM.
4. Implement mock runtime and scenario fixtures before provider integration.
5. Expose read-only run/ledger views before live controls.
6. Add UI on stable contracts; keep finance authorization server-side.
7. Add real-model provider adapter behind configuration only after safety and mock acceptance gates.

## 6. Definition of done for each change

- Requirements and affected trust boundary are identified.
- Tests cover success, denial, malformed input, retry/idempotency, and isolation where relevant.
- Focused tests and relevant build/lint checks pass; commands and results are reported accurately.
- No secret, real financial destination, or unsafe execution primitive is introduced.
- Documentation/contracts are updated with behavior changes.
- Review confirms the ledger remains authoritative and event evidence supports behavioral claims.

## 7. Open decisions to resolve before implementation

- Exact supported .NET, PostgreSQL, Python, and frontend versions at implementation time.
- Authentication boundary for local prototype and OIDC transition criteria.
- Whether to implement ASP.NET API plus Python worker as separate processes in Phase 0, or stage the worker behind a process boundary later.
- Event hash canonicalization, retention, and export redaction policy.
- Savings semantics and whether savings is liquid or restricted in MVP.
- Exact scenario fixture format and deterministic random-number generator/version.
- Provider list, cost accounting method, and secret-manager choice; none should be hard-coded into prompts.

## 8. First milestone acceptance checklist

- [ ] Repository skeleton and local setup instructions exist.
- [ ] Compose database starts with non-production credentials and health check.
- [ ] API, web, and runtime have documented health/start commands.
- [ ] CI runs formatting/static checks and deterministic tests.
- [ ] `.env.example` contains placeholders only, never credentials.
- [ ] No financial mutation bypass or real-money integration is present.
