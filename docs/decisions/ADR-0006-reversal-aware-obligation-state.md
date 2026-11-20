# FINMIND ADR 0006: Cash Basis and Reversal-Aware Obligation State

- Status: Implemented for Phase 1 local API
- Date: 20 November 2026
- Scope: Phase 1 finance boundary — full journal reversal, obligation payment state, and their database protection

## Context

The specification requires corrections to be made with a new linked
journal; committed history is never edited or deleted. `JournalKind.Reversal`
exists in code but is always rejected, and the database does not accept a
`reversal` journal kind.

FINMIND uses cash-basis accounting. Creating an obligation stores a row in
`obligations` and does not create a journal (ADR-0003). Paying an obligation
creates one `bill_payment` journal (agent cash → merchant), sets
`paid = true`, and stores the journal in `paid_entry_id`.

If a payment journal is reversed but the obligation stays paid, the
obligation reports a settlement that the ledger no longer contains.

## Decision summary

- Keep cash-basis accounting for the MVP. Do not add accrual accounting,
  expense accounts, or accounts payable.
- Support **full reversal only** of committed `bill_payment`, `transfer`,
  and `savings_move` journals. Partial reversal is not supported.
- Reversing a `bill_payment` returns its obligation to UNPAID in the same
  database transaction.
- Reversal is append-only: it adds a new journal and never changes or
  deletes the original journal or its postings.
- Only a researcher or the scenario engine may reverse. The agent has no
  reversal tool.
- Schema changes and database protection are delivered in migration
  `005_journal_reversal.sql`.

### Behavior

| Operation | Obligation state | Journal |
|---|---|---|
| CreateObligation | UNPAID | None |
| PayObligation | PAID | `bill_payment` |
| Reverse the `bill_payment` | UNPAID | New `reversal` |
| PayObligation again (new idempotency key) | PAID | New `bill_payment` |

Guiding principle: **an obligation is paid only while its associated payment
journal remains effective.**

## Invariants

| Invariant | Rule |
|---|---|
| Payment effectiveness | An obligation is PAID only if `paid_entry_id` refers to a `bill_payment` journal for that obligation that has not been reversed. An UNPAID obligation has no unreversed `bill_payment` for it. At most one unreversed `bill_payment` exists per obligation. |
| Reversal uniqueness | A journal can be reversed at most once. A reversal journal cannot itself be reversed. |
| Ledger integrity | A reversal has exactly the original postings' accounts with each amount negated, so original plus reversal sums to zero for every account. |

The append-only rule from ADR-0001 continues to apply to both the original
journal and the reversal.

## Reversible journals

| Original kind | Reversible? | Notes |
|---|---|---|
| `bill_payment` | Yes | Obligation returns to UNPAID |
| `transfer` | Yes | No obligation |
| `savings_move` | Yes | No obligation |
| `opening_funding` | No | Run capital; create a new run instead |
| `reversal` | No | To restore an effect, make a new payment or transfer |
| Legacy `bill_payment` with no `obligation_id` | No | Same approach as ADR-0002 legacy runs: fail closed and create a new run |

## Idempotency and response contract

An idempotency key identifies the original operation, not the current state
of an obligation.

- Retrying `pay-1` after its journal was reversed returns the original
  journal and never creates a new payment.
- To pay again, the caller must use a new idempotency key.
- `committed: true` means the original journal was durably recorded. It does
  **not** mean the journal is still in effect.

Add two fields to every journal success response (funding, transfer,
savings, bill payment, reversal), whether new or replayed:

| Field | Meaning |
|---|---|
| `effective` | `true` if this journal has not been reversed; `false` if it has |
| `reversedByJournalId` | ID of the reversal journal, or `null` |

Example: retrying a reversed payment.

```json
{
  "status": "replayed",
  "reasonCode": null,
  "committed": true,
  "journalId": "<original bill_payment id>",
  "wasIdempotentReplay": true,
  "effective": false,
  "reversedByJournalId": "<reversal id>"
}
```

The existing API uses `status: "replayed"` and `wasIdempotentReplay: true`
rather than a separate `replayed` field; both stay as defined in ADR-0005.
`CommitResult` gains `Effective` and `ReversedByJournalId` so both ledgers
return the same information.

## Reversal request

Ledger method (both `InMemoryLedger` and `PostgresLedger`, through
`IFinanceLedger`):

`ReverseJournal(runId, idempotencyKey, originalJournalId, reason, initiatorType)`

- `reason`: required, non-blank, stored on the reversal journal.
- `initiatorType`: `researcher` or `scenario_engine`. It is set by the
  trusted server path, never taken from a request body or model output.
- Reversal reuses the run-scoped idempotency key space. Same key with the
  same original journal and reason is a replay; any difference is
  `IDEMPOTENCY_KEY_CONFLICT`.

Local internal endpoint:
`POST /internal/runs/{runId}/journals/{journalId}/reversals` with body
`{ "idempotencyKey": "...", "reason": "..." }`. The endpoint sets
`initiatorType = researcher`.

Honest limitation: like all current internal endpoints, this endpoint is
unauthenticated and localhost-only. "Researcher only" is not yet enforced
by authentication; it holds because no agent tool gateway exists. The
Phase 2 tool gateway must never expose reversal, and authentication work
must restrict this endpoint to researcher roles.

### Policy for the reversal itself

- The per-run transaction cap (ADR-0002) applies to agent actions and does
  **not** apply to reversals.
- No-overdraft still applies. Reversing a `savings_move` or an
  agent-to-agent `transfer` takes money back from an agent account; if
  that account no longer holds enough, the reversal is denied with
  `INSUFFICIENT_FUNDS` and nothing changes.

### New reason codes

Added to the ADR-0005 catalog, which becomes `finance-reasons/v2`
(additive; no v1 code changes meaning):

| Code | Outcome | When |
|---|---|---|
| `JOURNAL_NOT_FOUND` | denied | Original journal missing or in another run |
| `JOURNAL_ALREADY_REVERSED` | denied | Original already has a reversal (with a different key) |
| `JOURNAL_NOT_REVERSIBLE` | denied | Opening funding, a reversal, or a legacy bill payment without `obligation_id` |
| `UNSUPPORTED_INITIATOR_TYPE` | malformed | Server code passed an initiator type outside the allowed set |

Missing `reason` uses `MISSING_REQUIRED_FIELD`.

## Schema changes (migration 005)

On `journal_entries`:

- Allow `kind = 'reversal'`.
- Add `reverses_entry_id UUID NULL` with foreign key
  `(reverses_entry_id, run_id, currency)` →
  `journal_entries (id, run_id, currency)`, so a reversal stays in its run.
- Add a unique partial index on `reverses_entry_id` (reversal uniqueness
  backstop).
- Add `obligation_id UUID NULL` with foreign key `(obligation_id, run_id)`
  → `obligations (id, run_id)`.
- Add `reversal_reason TEXT NULL` and `initiator_type TEXT NULL`
  (`CHECK initiator_type IN ('researcher', 'scenario_engine')`).
- Row checks:
  - `kind = 'reversal'` ⇔ `reverses_entry_id`, non-blank `reversal_reason`,
    and `initiator_type` are all present. Other kinds have all three NULL.
  - New `bill_payment` rows must have `obligation_id`. This check is added
    `NOT VALID`, so it applies to new rows without rewriting committed
    legacy rows.
  - Only `bill_payment` and reversals of `bill_payment` may carry
    `obligation_id`.

Existing journals are not updated or backfilled. Payment history comes from
`obligation_id` on new journals, not from parsing `description`.

Grants for `finmind_api` do not change: it keeps INSERT on
`journal_entries` and `UPDATE (paid, paid_entry_id)` on `obligations`.

## Database protection

Three triggers enforce the invariants in PostgreSQL, so application code is
not the only safeguard.

1. **Journal shape trigger (existing, extended).** For `reversal`, the
   existing deferred balance/shape trigger accepts the journal only if the
   reversal-specific checks below pass.

2. **Reversal validity — deferred constraint trigger** on `journal_entries`
   and `journal_postings` inserts, checked at commit:
   - The original exists in the same run, is committed, and its kind is
     reversible.
   - The reversal has exactly two postings: the same accounts as the
     original, each with the negated amount.
   - For a `bill_payment` original, the reversal's `obligation_id` equals the
     original's `obligation_id` (which must be non-NULL).

   Deferring to commit lets the application insert the reversal header and
   postings in any order within the transaction.

3. **Obligation state guard — immediate `BEFORE UPDATE` trigger** on
   `obligations`:
   - PAID → UNPAID is allowed only if a `reversal` journal exists with
     `reverses_entry_id = OLD.paid_entry_id` and `obligation_id = OLD.id`.
   - UNPAID → PAID is allowed only if `NEW.paid_entry_id` is a `bill_payment`
     with `obligation_id = OLD.id` and no reversal.
   - Changing `paid_entry_id` while the obligation stays PAID is rejected.

   Because the guard only checks linkage, it relies on trigger 2 for
   posting correctness. Both run in the same transaction: if the reversal
   postings are wrong, the commit fails and the obligation update rolls
   back with it.

4. **Payment effectiveness — deferred constraint trigger** on `obligations`
   updates and `bill_payment` / `reversal` inserts, checked at commit for each
   affected obligation: the invariant in the table above holds.

### Required operation order

The guard in (3) looks for the reversal journal when the obligation row is
updated, so the reversal must be inserted first:

1. Begin a `READ COMMITTED` transaction.
2. Lock the run row `FOR UPDATE` (existing per-run serialization).
3. Load the original journal and its postings. Check for an existing
   reversal and handle idempotency.
4. For a `bill_payment`, lock the obligation row `FOR UPDATE` and confirm
   `paid_entry_id` is the original journal.
5. Check no-overdraft for agent accounts.
6. Insert the reversal journal header and its two negated postings.
7. For a `bill_payment`, update the obligation: `paid = false`,
   `paid_entry_id = NULL`.
8. Commit. Deferred triggers 2 and 4 run here; any violation rolls back the
   whole transaction.

The run lock serializes reversals and payments within a run. The unique
index on `reverses_entry_id` is a final backstop against double reversal.

## Retired path

`PayBill` (a bill payment not bound to an obligation) remains in both ledger
classes but is not used by the API. Once new `bill_payment` rows require
`obligation_id`, it cannot satisfy the schema. The implementation removes
`PayBill` and moves its tests to `PayObligation`. This is consistent with
the technical details: a bill payment must bind to a server-known
obligation.

## Implementation notes

- Migration: `infra/db/migrations/005_journal_reversal.sql` (re-runnable).
- Ledgers: `ReverseJournal` in `InMemoryLedger` and `PostgresLedger`;
  shared checks in `LedgerRequestValidation`.
- `CommitResult` has `Effective` and `ReversedByJournalId`; `LedgerJournal`
  has `ObligationId`, `ReversesJournalId`, `ReversalReason`, and `Initiator`.
- `PayBill` was removed. `OBLIGATION_AMOUNT_MISMATCH` is no longer emitted
  but stays in the catalog because codes are never removed or reused.
- Tests: `ReversalTests` runs the behavior scenarios against both ledgers,
  plus PostgreSQL-only tests for the restricted role, concurrency, and
  owner-role bypass attempts.

## Audit events (deferred to Step 4)

Reserve the event names `JOURNAL_REVERSED` and
`OBLIGATION_PAYMENT_REVERSED`. They are written when the audit event store
exists, in the same transaction as the reversal. Until then, the audit
trail is the immutable original journal, the reversal journal
(`reverses_entry_id`, `obligation_id`, `reversal_reason`,
`initiator_type`), and the obligation's current state. No new obligation
status values are introduced.

## Research interpretation

A reversal is an environment event caused by a researcher or scenario, not
an agent action. How the agent responds (notices the UNPAID obligation,
retries the old key, pays with a new key, or ignores it) is observable
behavior. Record what happened; do not infer intent. Reports must
distinguish committed journals from effective journals.

## Acceptance tests (implementation)

Both ledgers unless noted:

- Full reversal of each reversible kind restores every account balance and
  leaves the original journal unchanged.
- Reversing a `bill_payment` sets the obligation to UNPAID; paying again
  with a new key sets it to PAID with the new journal.
- Retrying the original payment key after reversal returns `replayed`,
  `committed: true`, `effective: false`, and the reversal ID, with no
  balance change.
- Double reversal with a new key is `JOURNAL_ALREADY_REVERSED`; with the
  same key it is a replay.
- Opening funding, reversals, and legacy bill payments are
  `JOURNAL_NOT_REVERSIBLE`.
- A reversal that would overdraw an agent account is `INSUFFICIENT_FUNDS`
  with no state change.
- The cap is not applied to reversals. Because the cap is immutable and the
  original was within it, a reversal cannot exceed it, so this has no
  separate test.
- Cross-run reversal is `JOURNAL_NOT_FOUND`.
- PostgreSQL: concurrent reversals of one journal produce exactly one
  reversal.
- PostgreSQL, as the owner role bypassing the application: a mismatched
  reversal posting, a PAID → UNPAID update without a reversal, an UNPAID →
  PAID update pointing to a reversed payment, and a second unreversed
  payment for one obligation all fail at the database.
- PostgreSQL, as `finmind_api`: the valid reversal flow succeeds, and
  direct UPDATE/DELETE of journals and postings still fails.
- Reconciliation: balances equal the sum of postings after every reversal.
