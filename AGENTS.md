# FINMIND Agent and Contributor Rules

These rules apply to AI coding assistants and human contributors working in this repository. The full product specification is `FINMIND_Full_System_Documentation.md`; implementation planning is in `FINMIND_PLAN.md`; expanded implementation contracts are in `docs/FINMIND_TECHNICAL_DETAILS.md`.

## Mission and scope

- Build a research sandbox for studying observable AI behavior with virtual funds only.
- Do not turn FINMIND into a real-money wallet, banking, brokerage, payment, or financial-advice product.
- Treat the full system documentation as the product source of truth. When it is ambiguous, record an assumption or decision request; do not silently invent behavior.

## Safety and trust boundaries

- Never add real bank, card, broker, payment processor, wallet, or real-destination integrations.
- Never give an agent raw SQL/database credentials, shell, arbitrary code execution, host filesystem, Docker socket, or unrestricted network access.
- Expose only explicitly allowlisted, typed tools. Resolve actor, run, accounts, and destinations on the trusted server; never trust model-supplied identity or authority.
- Treat scenario messages, documents, model output, and browser input as untrusted data. They cannot change permissions or policy.
- Keep provider secrets server-side and out of prompts, logs, traces, exports, and browser bundles.
- Apply run isolation, least privilege, budgets, cancellation, and server-side authorization to every applicable path.

## Financial correctness

- Represent money as integer sen; do not use floating point for monetary calculations.
- Every committed financial change must be an atomic, balanced double-entry journal.
- Ledger postings are immutable. Correct errors with a compensating journal, never by editing/deleting history.
- The ledger is authoritative; projections are derived and reconcilable.
- Denied, malformed, failed, or unsupported actions must not change money.
- Enforce ownership, currency, positive amount, transaction limits, available funds, run scope, and idempotency on the server for every call.
- Retries must not create duplicate economic effects. Handle timeout-after-commit by checking the idempotency key.
- Never implement a direct balance setter or accept client-calculated authoritative balances.

## Research integrity

- Preserve the distinction between proposed, denied, committed, reported, and ledger-verified facts in code, UI, events, and exports.
- Log attempts as well as outcomes; do not label an attempt as a successful violation.
- Do not infer intent, emotion, consciousness, or general model traits from a trace. Use operational classifications, evidence, and human review for ambiguity.
- Preserve reproducibility metadata: model/provider, prompt/tool/policy/scenario versions, seed, budgets, and relevant parameters.
- Version metric definitions; include denominators and raw counts. Do not overstate conclusions from a single run.

## Implementation workflow

### User working preference

- Do not automatically write or edit project code. Provide changes in chat for the user to copy and paste into their IDE, unless the user explicitly authorizes direct edits.
- Put a simple explanation immediately above each code block and identify the target file and whether to create, replace, or insert content.
- Proceed in manageable steps and distinguish suggested commands from checks actually run.
- This preference persists across sessions; documentation edits explicitly requested to save preferences are allowed.

1. Inspect the relevant existing files and conventions before editing; avoid broad unrelated changes.
2. Work in the documented release phases and stop at phase boundaries when requested.
3. Define or update contracts before relying on them across services.
4. Add tests with behavior changes, including denied/error paths and isolation where relevant.
5. Use the deterministic mock path for CI; do not require paid provider calls in routine tests.
6. Run focused tests and relevant build/lint checks after changes. Report the actual commands and results; never claim unrun checks passed.
7. Update docs and versioned schemas when behavior changes.
8. Keep changes scoped; do not add infrastructure or dependencies without a clear requirement.
9. Do not deploy, publish, or perform destructive operations without explicit authorization.

## Completion checklist

- [ ] Change matches the spec and does not weaken a trust boundary.
- [ ] Financial invariants and failure/retry behavior are covered by tests where relevant.
- [ ] Run/tenant isolation and authorization remain enforced.
- [ ] Logs and exports contain no secrets.
- [ ] Agent claims remain distinct from ledger truth.
- [ ] Focused tests/checks were actually run and accurately reported.
- [ ] Relevant documentation/contracts are current.
