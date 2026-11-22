using System.Text.Json.Serialization;

namespace Finmind.Api.Finance;

// HTTP mapping for finance outcomes (ADR-0005). Pure functions so it can be unit tested without hosting.
public sealed record LedgerErrorBody(string Status, string ReasonCode, string Message, bool? Committed, Guid? JournalId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Fields = null);

public static class LedgerHttpResponses
{
    public const string GenericInternalErrorMessage = "Internal error. The outcome is unknown: it may or may not have committed. Retry with the same idempotency key or request ID.";
    public const string ReadFailedMessage = "Internal error. This read changed nothing.";

    public static int StatusCodeFor(LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.Committed or LedgerOutcome.Replayed => 200,
        LedgerOutcome.Malformed => 400,
        LedgerOutcome.Denied or LedgerOutcome.UnsupportedCapability => 422,
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
            outcome == LedgerOutcome.InternalError ? null : false, // null: unknown (ADR-0013 A2)
            null));
    }

    public static (int StatusCode, LedgerErrorBody Body) ForException(Exception exception) => exception switch
    {
        RunConfigException config => (400, ForError(config.ReasonCode, config.Message).Body with { Fields = config.Fields }),
        LedgerException ledger => ForError(ledger.ReasonCode, ledger.Message),
        OverflowException => ForError(LedgerReasonCode.InvalidAmount, "Amount exceeds supported range."),
        _ => ForError(LedgerReasonCode.InternalError, GenericInternalErrorMessage)
    };

    // Read endpoints report missing resources as 404 while keeping the same body shape.
    public static (int StatusCode, LedgerErrorBody Body) ForReadException(Exception exception)
    {
        var (statusCode, body) = ForException(exception);
        var isNotFound = exception is LedgerException { ReasonCode: LedgerReasonCode.RunNotFound or LedgerReasonCode.AccountNotInRun or LedgerReasonCode.ObligationNotFound };
        if (statusCode >= 500) body = body with { Message = ReadFailedMessage, Committed = false };
        return (isNotFound ? 404 : statusCode, body);
    }

    public static string StatusFor(CommitResult result) =>
        LedgerReasonCodes.ToWireValue(result.WasIdempotentReplay ? LedgerOutcome.Replayed : LedgerOutcome.Committed);
}
