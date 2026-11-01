# FINMIND ADR 0003: Run-Scoped Invoice Identity

- Status: Proposed
- Date: 31 October 2026
- Scope: Prevent duplicate synthetic invoices becoming separately payable obligations

## Context

An existing obligation cannot be paid twice, but obligation creation
currently generates a new ID for every request. Repeated delivery of
one invoice can therefore create multiple payable obligations.

## Proposed decision

- Require an invoice reference when creating an obligation.
- The trusted scenario/setup component assigns the reference.
  Agent text cannot establish invoice identity or payment authority.
- Invoice references are unique within a run.
- References are case-sensitive, nonblank, and must have no leading
  or trailing whitespace. Reject invalid references rather than
  silently normalizing them.
- Repeating a reference with the same merchant and amount returns
  the existing obligation, including its current payment status.
- Repeating a reference with a different merchant or amount raises
  an idempotency conflict. It must not modify the existing obligation.
- Different runs may reuse the same reference.
- Concurrent creation requests must produce only one obligation.
- Payment continues to resolve the amount and merchant from the
  stored obligation. Creation and retries never move money.

## Existing data

Assign existing obligations a reserved reference in the form
`legacy:<obligation UUID>`. Reject this prefix for new creation requests.

This preserves existing IDs and payment links. It cannot identify or
deduplicate historical invoices that lacked a recorded reference.

## Implementation

- Add invoice_reference to obligations.
- Enforce uniqueness on (run_id, invoice_reference).
- Implement equivalent behavior in PostgreSQL and in-memory ledgers.
- Update the internal setup request and obligation response.
- Map conflicting creation requests to HTTP 409.
- Keep the internal setup endpoint localhost-only until caller
  authentication and authorization are implemented.

## Acceptance tests

- Identical creation retries return the same obligation ID.
- Changed merchant or amount conflicts without modifying state.
- Identical references are allowed in separate runs.
- Blank, padded, and reserved references are rejected.
- Concurrent identical requests create one obligation.
- Retrying creation after payment returns the paid obligation.
- Duplicate delivery followed by payment has one economic effect.