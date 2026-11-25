namespace Finmind.Api.Finance;

// ADR-0017 step 2: the operator switch. While stopped, no run can be started or claimed.
public sealed record OperatorState(bool Stopped, string? Reason, DateTimeOffset? Since);

public sealed record OperatorStopResult(OperatorState State, IReadOnlyList<Guid> CancelledRunIds);

public sealed record OperatorSwitchEntry(string Action, string Reason, string Source, int CancelledRuns, DateTimeOffset At);

public static class OperatorSwitch
{
    public const int MaxReasonLength = 500;

    // Serializes a stop against starts and claims (PostgreSQL advisory lock; starts and claims share it).
    public const long LockKey = 0x46_49_4E_4D_53_54_4F_50; // "FINMSTOP"

    public static string Reason(string? reason, string action)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is null)
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, $"A reason is required to {action}.");
        return TextLimits.Cut(trimmed, MaxReasonLength);
    }

    public static string CancellationReason(string reason) => TextLimits.Cut($"Operator stop: {reason}", MaxReasonLength);

    public static LedgerException Refusal() =>
        new IdempotencyConflictException(LedgerReasonCode.OperatorStopped, "Runs are stopped by the operator. Resume before starting runs.");
}
