using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0009: types shared by both ledgers' gateway-support methods and the ToolGateway.
public enum ToolCallBeginKind { New, Resume, Replay, Conflict }

// Denial is set when the call was recorded but must not execute (run not active, budget exhausted).
public sealed record ToolCallBegin(ToolCallBeginKind Kind, JsonObject? StoredResponse, LedgerReasonCode? Denial);

public sealed record ToolCallCompletion(JsonObject Response, string Status, string? ReasonCode, Guid? JournalId, PendingEvent? ExtraEvent);

// ADR-0011: Replayed is true when this turn was already recorded (a worker retry); tokens are not counted twice.
public sealed record ModelInvocationResult(LedgerReasonCode? Denial, bool Replayed, long TokensUsed, long CostMicroUsd = 0);

// ADR-0011 / ADR-0012: one agent turn as reported by the worker. Cost is computed by the server.
public sealed record ModelInvocation(
    int Turn, long InputTokens, long OutputTokens, long LatencyMs, string OutputText,
    long CacheReadInputTokens = 0, long CacheCreationInputTokens = 0, string? Model = null,
    string? StopReason = null, string? RefusalCategory = null, int ToolUseCount = 0)
{
    // The token budget counts new tokens; cache reads are excluded (their cost is still metered).
    public long BudgetTokens => checked(InputTokens + OutputTokens + CacheCreationInputTokens);
}

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

    public static long CostOf(JsonObject snapshot, ModelInvocation invocation) =>
        PricingCatalog.CostMicroUsd(Provider(snapshot), ModelId(snapshot), invocation.InputTokens, invocation.OutputTokens,
            invocation.CacheReadInputTokens, invocation.CacheCreationInputTokens);

    public static int MaxActions(JsonObject snapshot) => (int)snapshot["maxActions"]!.GetValue<long>();

    public static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.BudgetExceeded;

    // Stable order for both ledgers: due day, then invoice reference.
    public static IReadOnlyList<ObligationResult> Order(IEnumerable<ObligationResult> obligations) =>
        obligations.OrderBy(o => o.DueDay ?? int.MaxValue).ThenBy(o => o.InvoiceReference, StringComparer.Ordinal).ToArray();
}
