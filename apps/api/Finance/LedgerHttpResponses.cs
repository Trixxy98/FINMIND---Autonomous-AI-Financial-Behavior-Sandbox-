namespace Finmind.Api.Finance;

// HTTP mapping for finance outcomes (ADR-0005). Pure functions so it can be unit tested without hosting.
public sealed record LedgerErrorBody(string Status, string ReasonCode, string Message, bool Committed, Guid? JournalId);

public static class LedgerHttpResponses
{
    public const string GenericInternalErrorMessage = "Internal error. The request did not change ledger state.";

    public static int StatusCodeFor(LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.Committed or LedgerOutcome.Replayed => 200,
        LedgerOutcome.Malformed => 400,
        LedgerOutcome.Denied => 422,
        LedgerOutcome.Conflict => 409,
        _ => 500
    };

    public static (int StatusCode, LedgerErrorBody Body) ForError(LedgerReasonCode code, string message)
    {
        var outcome = LedgerReasonCodes.OutcomeOf(code);
        // Internal errors never expose diagnostic text to callers.
        var safeMessage = outcome == LedgerOutcome.InternalError ? GenericInternalErrorMessage : message;
        return (StatusCodeFor(outcome), new LedgerErrorBody(
            LedgerReasonCodes.ToWireValue(outcome),
            LedgerReasonCodes.ToWireValue(code),
            safeMessage,
            false,
            null));
    }

    public static (int StatusCode, LedgerErrorBody Body) ForException(Exception exception) => exception switch
    {
        LedgerException ledger => ForError(ledger.ReasonCode, ledger.Message),
        OverflowException => ForError(LedgerReasonCode.InvalidAmount, "Amount exceeds supported range."),
        _ => ForError(LedgerReasonCode.InternalError, GenericInternalErrorMessage)
    };

    // Read endpoints report missing resources as 404 while keeping the same body shape.
    public static (int StatusCode, LedgerErrorBody Body) ForReadException(Exception exception)
    {
        var (statusCode, body) = ForException(exception);
        var isNotFound = exception is LedgerException { ReasonCode: LedgerReasonCode.RunNotFound or LedgerReasonCode.AccountNotInRun or LedgerReasonCode.ObligationNotFound };
        return (isNotFound ? 404 : statusCode, body);
    }

    public static string StatusFor(CommitResult result) =>
        LedgerReasonCodes.ToWireValue(result.WasIdempotentReplay ? LedgerOutcome.Replayed : LedgerOutcome.Committed);
}
