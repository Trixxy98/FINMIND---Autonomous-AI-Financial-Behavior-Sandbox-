using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0009: types shared by both ledgers' gateway-support methods and the ToolGateway.
public enum ToolCallBeginKind { New, Resume, Replay, Conflict }

// Denial is set when the call was recorded but must not execute (run not active, budget exhausted).
public sealed record ToolCallBegin(ToolCallBeginKind Kind, JsonObject? StoredResponse, LedgerReasonCode? Denial);

public sealed record ToolCallCompletion(JsonObject Response, string Status, string? ReasonCode, Guid? JournalId, PendingEvent? ExtraEvent)
{
    public bool IsRejection => Status is not ("ok" or "committed" or "replayed");
}

// ADR-0011: Replayed is true when this turn was already recorded (a worker retry); tokens are not counted twice.
public sealed record ModelInvocationResult(LedgerReasonCode? Denial, bool Replayed, long TokensUsed, long CostMicroUsd = 0);

// ADR-0011 / ADR-0012: one agent turn as reported by the worker. Cost is computed by the server.
public sealed record ModelInvocation(
    int Turn, long InputTokens, long OutputTokens, long LatencyMs, string OutputText,
    long CacheReadInputTokens = 0, long CacheCreationInputTokens = 0, string? Model = null,
    string? StopReason = null, string? RefusalCategory = null, int ToolUseCount = 0,
    // ADR-0013 A1: how many attempts this report settles, from the first. Null means all. Others stay reserved.
    int? SettledAttempts = null)
{
    // The token budget counts new tokens; cache reads are excluded (their cost is still metered).
    public long BudgetTokens => checked(InputTokens + OutputTokens + CacheCreationInputTokens);
}

// ADR-0013 A1: permission for one model request, granted before it is sent.
public sealed record ModelReservation(int Turn, int Attempt, long InputTokens, long MaxOutputTokens);
public sealed record ModelReservationResult(LedgerReasonCode? Denial, long MaxOutputTokens, long ReservedMicroUsd, long CommittedMicroUsd);

public sealed record AgentMerchant(Guid AccountId, string Key, string Label);

public sealed record AgentView(Guid CashAccountId, Guid? SavingsAccountId, IReadOnlyList<AgentMerchant> Merchants,
    IReadOnlyList<ObligationResult> Obligations);

public static class AgentGatewayRules
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    public const string BudgetExhaustedReason = "Action budget exhausted.";
    public const string WallTimeExhaustedReason = "Wall-time budget exhausted.";
    public const string TokenExhaustedReason = "Token budget exhausted.";
    public static int MaxWallTimeSeconds(JsonObject snapshot) => (int)snapshot["maxWallTimeSeconds"]!.GetValue<long>();
    public static long MaxModelTokens(JsonObject snapshot) => snapshot["maxModelTokens"]!.GetValue<long>();
    public const string CostExhaustedReason = "Cost budget exhausted.";
    public static long MaxCostMicroUsd(JsonObject snapshot) => snapshot["maxCostMicroUsd"]?.GetValue<long>() ?? 0;
    public static string Provider(JsonObject snapshot) => snapshot["agent"]!["provider"]!.GetValue<string>();
    public static string ModelId(JsonObject snapshot) => snapshot["agent"]!["modelId"]!.GetValue<string>();

    public static long CostOf(JsonObject snapshot, ModelInvocation invocation, int requests) =>
        PricingCatalog.CostMicroUsd(Provider(snapshot), ModelId(snapshot), invocation.InputTokens, invocation.OutputTokens,
            invocation.CacheReadInputTokens, invocation.CacheCreationInputTokens, requests);

    public const string SessionExpiredReason = "Worker session expired.";
    private const long MinimumOutputTokens = 1_024;

    // committed = recorded cost + outstanding reservations. Null when not even a minimal answer fits under the cap.
    public static (long MaxOutputTokens, long CostMicroUsd)? Grant(JsonObject snapshot, long committed, ModelReservation request)
    {
        if (Provider(snapshot) != "anthropic") return (request.MaxOutputTokens, 0);
        var model = ModelId(snapshot);
        var requested = Math.Min(request.MaxOutputTokens, Number(snapshot["agent"]!["settings"]!.AsObject(), "maxOutputTokensPerTurn"));
        var granted = Math.Min(requested,
            PricingCatalog.AffordableOutputTokens(model, request.InputTokens, MaxCostMicroUsd(snapshot) - committed));
        if (granted < Math.Min(MinimumOutputTokens, requested)) return null;
        return (granted, PricingCatalog.WorstCaseMicroUsd(model, request.InputTokens, granted));
    }

    public static JsonObject[] Settled(IEnumerable<JsonObject> reservations, ModelInvocation invocation) =>
        reservations.Where(p => invocation.SettledAttempts is not { } count || Number(p, "attempt") <= count).ToArray();

    public static long Number(JsonObject source, string key) => ToolDefinition.TryGetInteger(source[key], out var number)
        ? number : throw new InvalidOperationException($"{key} is not an integer.");

    public static int MaxActions(JsonObject snapshot) => (int)snapshot["maxActions"]!.GetValue<long>();

    public static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.BudgetExceeded;

    // Stable order for both ledgers: due day, then invoice reference.
    public static IReadOnlyList<ObligationResult> Order(IEnumerable<ObligationResult> obligations) =>
        obligations.OrderBy(o => o.DueDay ?? int.MaxValue).ThenBy(o => o.InvoiceReference, StringComparer.Ordinal).ToArray();
}
