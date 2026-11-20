namespace Finmind.Api.Finance;

// Outcome and reason code catalog v1. See docs/decisions/ADR-0005-policy-decisions-and-reason-codes.md.
// Wire values are a published contract: append new codes, never rename or reuse existing ones.
public enum LedgerOutcome { Committed, Replayed, Malformed, Denied, Conflict, InternalError }

public enum LedgerReasonCode
{
    MissingRequiredField,
    InvalidAmount,
    InvalidInvoiceReference,
    UnsupportedAccountKind,
    SameSourceAndDestination,
    RunNotFound,
    RunPolicyNotConfigured,
    RunAlreadyFunded,
    AccountNotInRun,
    AccountKindNotPermitted,
    ExceedsTransactionLimit,
    InsufficientFunds,
    ObligationNotFound,
    ObligationAlreadyPaid,
    ObligationAmountMismatch,
    IdempotencyKeyConflict,
    InvoiceReferenceConflict,
    RunPolicyConflict,
    ConcurrentStateChange,
    LedgerInvariantViolation,
    InternalError
}

public static class LedgerReasonCodes
{
    public const string CatalogVersion = "finance-reasons/v1";

    public static LedgerOutcome OutcomeOf(LedgerReasonCode code) => code switch
    {
        LedgerReasonCode.MissingRequiredField
            or LedgerReasonCode.InvalidAmount
            or LedgerReasonCode.InvalidInvoiceReference
            or LedgerReasonCode.UnsupportedAccountKind
            or LedgerReasonCode.SameSourceAndDestination => LedgerOutcome.Malformed,
        LedgerReasonCode.RunNotFound
            or LedgerReasonCode.RunPolicyNotConfigured
            or LedgerReasonCode.RunAlreadyFunded
            or LedgerReasonCode.AccountNotInRun
            or LedgerReasonCode.AccountKindNotPermitted
            or LedgerReasonCode.ExceedsTransactionLimit
            or LedgerReasonCode.InsufficientFunds
            or LedgerReasonCode.ObligationNotFound
            or LedgerReasonCode.ObligationAlreadyPaid
            or LedgerReasonCode.ObligationAmountMismatch => LedgerOutcome.Denied,
        LedgerReasonCode.IdempotencyKeyConflict
            or LedgerReasonCode.InvoiceReferenceConflict
            or LedgerReasonCode.RunPolicyConflict => LedgerOutcome.Conflict,
        LedgerReasonCode.ConcurrentStateChange
            or LedgerReasonCode.LedgerInvariantViolation
            or LedgerReasonCode.InternalError => LedgerOutcome.InternalError,
        _ => LedgerOutcome.InternalError
    };

    public static string ToWireValue(LedgerReasonCode code) => code switch
    {
        LedgerReasonCode.MissingRequiredField => "MISSING_REQUIRED_FIELD",
        LedgerReasonCode.InvalidAmount => "INVALID_AMOUNT",
        LedgerReasonCode.InvalidInvoiceReference => "INVALID_INVOICE_REFERENCE",
        LedgerReasonCode.UnsupportedAccountKind => "UNSUPPORTED_ACCOUNT_KIND",
        LedgerReasonCode.SameSourceAndDestination => "SAME_SOURCE_AND_DESTINATION",
        LedgerReasonCode.RunNotFound => "RUN_NOT_FOUND",
        LedgerReasonCode.RunPolicyNotConfigured => "RUN_POLICY_NOT_CONFIGURED",
        LedgerReasonCode.RunAlreadyFunded => "RUN_ALREADY_FUNDED",
        LedgerReasonCode.AccountNotInRun => "ACCOUNT_NOT_IN_RUN",
        LedgerReasonCode.AccountKindNotPermitted => "ACCOUNT_KIND_NOT_PERMITTED",
        LedgerReasonCode.ExceedsTransactionLimit => "EXCEEDS_TRANSACTION_LIMIT",
        LedgerReasonCode.InsufficientFunds => "INSUFFICIENT_FUNDS",
        LedgerReasonCode.ObligationNotFound => "OBLIGATION_NOT_FOUND",
        LedgerReasonCode.ObligationAlreadyPaid => "OBLIGATION_ALREADY_PAID",
        LedgerReasonCode.ObligationAmountMismatch => "OBLIGATION_AMOUNT_MISMATCH",
        LedgerReasonCode.IdempotencyKeyConflict => "IDEMPOTENCY_KEY_CONFLICT",
        LedgerReasonCode.InvoiceReferenceConflict => "INVOICE_REFERENCE_CONFLICT",
        LedgerReasonCode.RunPolicyConflict => "RUN_POLICY_CONFLICT",
        LedgerReasonCode.ConcurrentStateChange => "CONCURRENT_STATE_CHANGE",
        LedgerReasonCode.LedgerInvariantViolation => "LEDGER_INVARIANT_VIOLATION",
        LedgerReasonCode.InternalError => "INTERNAL_ERROR",
        _ => "INTERNAL_ERROR"
    };

    public static string ToWireValue(LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.Committed => "committed",
        LedgerOutcome.Replayed => "replayed",
        LedgerOutcome.Malformed => "malformed",
        LedgerOutcome.Denied => "denied",
        LedgerOutcome.Conflict => "conflict",
        _ => "internal_error"
    };
}
