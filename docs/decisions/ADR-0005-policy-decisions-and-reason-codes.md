# FINMIND ADR 0005: Structured Finance Decisions and Stable Reason Codes

- Status: Implemented for Phase 1 local API
- Date: 20 November 2026
- Scope: Phase 1 finance boundary outcomes (both ledger implementations and the local internal API)

## Context

Both `InMemoryLedger` and `PostgresLedger` reject requests by throwing
`LedgerValidationException` or `IdempotencyConflictException` with
free-text messages. The API maps almost every rejection to HTTP 400 with
that message. Researchers and the future runtime cannot reliably tell a
limit denial from insufficient funds, a malformed request, or an
internal defect without parsing English text.

The specification (sections 7.2 and 9; technical details section 3)
requires every proposal to receive a decision with a stable reason code,
and requires the minimum outcomes: allowed, denied, malformed,
unsupported capability, duplicate/replayed, and internal error.

## Decision

### Outcome categories

Every finance boundary call ends in exactly one outcome:

| Outcome | Meaning | Money changed? |
|---|---|---|
| `committed` | A new journal was committed | Yes |
| `replayed` | Same idempotency key and payload; the original result is returned | No new effect |
| `malformed` | The request is structurally invalid | No |
| `denied` | The request is well formed but policy or state forbids it | No |
| `conflict` | An identity key was reused with different details | No |
| `internal_error` | A platform defect or transient failure | No (transaction rolled back) |

`unsupported_capability` is reserved for the Phase 2 tool gateway. The
finance boundary does not use it because it only exposes typed methods.

### Reason codes (catalog v1)

Codes are stable, upper snake case, and append-only. A code is never
renamed or reused for a different meaning. Each code belongs to exactly
one outcome.

| Code | Outcome | Current source examples |
|---|---|---|
| `MISSING_REQUIRED_FIELD` | malformed | Empty run ID, idempotency key, description, account ID, or account kind |
| `INVALID_AMOUNT` | malformed | Zero, negative, or overflowing sen amount |
| `INVALID_INVOICE_REFERENCE` | malformed | Blank, padded, or reserved `legacy:` reference |
| `UNSUPPORTED_ACCOUNT_KIND` | malformed | Unknown account kind |
| `SAME_SOURCE_AND_DESTINATION` | malformed | Transfer or payment whose two postings use one account |
| `RUN_NOT_FOUND` | denied | Run does not exist |
| `RUN_POLICY_NOT_CONFIGURED` | denied | Monetary action on a run without a stored cap |
| `RUN_ALREADY_FUNDED` | denied | Second opening funding or funding after activity |
| `ACCOUNT_NOT_IN_RUN` | denied | Account missing or owned by another run |
| `ACCOUNT_KIND_NOT_PERMITTED` | denied | Wrong account kind for the action or payee |
| `EXCEEDS_TRANSACTION_LIMIT` | denied | Amount above the stored per-run cap |
| `INSUFFICIENT_FUNDS` | denied | Agent account would be overdrawn |
| `OBLIGATION_NOT_FOUND` | denied | Obligation missing or in another run |
| `OBLIGATION_ALREADY_PAID` | denied | Payment of a paid obligation with a new key |
| `OBLIGATION_AMOUNT_MISMATCH` | denied | Payment amount differs from the stored obligation |
| `IDEMPOTENCY_KEY_CONFLICT` | conflict | Key reused with a different payload or operation |
| `INVOICE_REFERENCE_CONFLICT` | conflict | Invoice reference reused with different details |
| `RUN_POLICY_CONFLICT` | conflict | Re-initialization with a different cap or allocation |
| `CONCURRENT_STATE_CHANGE` | internal_error | Obligation state changed under the run lock |
| `LEDGER_INVARIANT_VIOLATION` | internal_error | Unbalanced, zero-value, or wrongly counted postings (only reachable through the generic `Commit` path) |
| `INTERNAL_ERROR` | internal_error | Unexpected stored value or unclassified failure |

When validation order means one request violates several rules, the
code reports the first failed check. Both ledgers share field, amount,
account-kind, and obligation validation (`LedgerRequestValidation`) and
use the same check order. Known difference: `InMemoryLedger` has no run
registry, so it cannot return `RUN_NOT_FOUND`; an unknown run surfaces as
`ACCOUNT_NOT_IN_RUN`, `OBLIGATION_NOT_FOUND`, or
`RUN_POLICY_NOT_CONFIGURED` instead.

### Implementation shape

- `LedgerReasonCode`, `LedgerOutcome`, and their wire values live in
  `apps/api/Finance/LedgerReasonCodes.cs` (catalog `finance-reasons/v1`).
- Both exception types derive from `LedgerException` and carry a required
  reason code. Messages remain human-readable diagnostics and are not
  part of the contract.
- The ledgers throw `MISSING_REQUIRED_FIELD` instead of `ArgumentException`.
  Checked-arithmetic `OverflowException` is mapped to `INVALID_AMOUNT` by
  the API; any other unexpected exception becomes `INTERNAL_ERROR`.
- `InMemoryLedger` and `PostgresLedger` implement `IFinanceLedger` and must
  return the same code for the same scenario. `ReasonCodeTests` runs one
  scenario table against both.
- Successful result records keep `Committed` and `WasIdempotentReplay`.

### API response shape

Journal endpoints (funding, transfers, savings, bill payments) and all
error responses use a uniform body (camelCase, matching
the existing API):

```json
{
  "status": "denied",
  "reasonCode": "EXCEEDS_TRANSACTION_LIMIT",
  "message": "Run transaction limit exceeded.",
  "committed": false,
  "journalId": null
}
```

Journal success responses use `status` `committed` or `replayed`,
`reasonCode` `null`, and keep existing success fields such as
`journalId`, `wasIdempotentReplay`, and `balanceSen`. Setup endpoints
(account and obligation creation) keep their existing success bodies
because they never move money; their errors use the uniform body.
HTTP response mapping lives in `apps/api/Finance/LedgerHttpResponses.cs`.

HTTP mapping: `committed`/`replayed` 200 (obligation and account
creation keep their current 200/201); `malformed` 400; `denied` 422;
`conflict` 409; `internal_error` 500 with a generic message only.
Read endpoints return 404 for `RUN_NOT_FOUND`, `ACCOUNT_NOT_IN_RUN`,
and `OBLIGATION_NOT_FOUND`, with the uniform error body. Malformed JSON
that ASP.NET cannot bind is still rejected by the framework with its
own 400 response before the handler runs.

## Out of scope

- Durable storage of decisions. Persisting proposals and decisions with
  sequence numbers belongs to the audit event store (planned ADR on event
  and ledger atomicity). This ADR defines the vocabulary those events
  will use.
- Authentication, run lifecycle, and the agent tool gateway.

## Research interpretation

A reason code describes which rule stopped a request. It does not
describe intent. A `denied` outcome is an observed attempt, not a
successful violation. `internal_error` indicates a platform problem
and must be reported separately from agent behavior.

## Acceptance tests

- Every rejection path in both ledgers produces a documented code.
- Same scenario gives the same code in `InMemoryLedger` and `PostgresLedger`.
- Every non-committed outcome leaves balances, journals, and obligations unchanged.
- Replays return `replayed` with the original journal ID and no new posting.
- API returns the documented status, reason code, and HTTP status for each outcome.
- `internal_error` responses contain no stack trace, SQL, or connection details.
