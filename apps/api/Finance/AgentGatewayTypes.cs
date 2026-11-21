using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0009: types shared by both ledgers' gateway-support methods and the ToolGateway.
public enum ToolCallBeginKind { New, Resume, Replay, Conflict }

// Denial is set when the call was recorded but must not execute (run not active, budget exhausted).
public sealed record ToolCallBegin(ToolCallBeginKind Kind, JsonObject? StoredResponse, LedgerReasonCode? Denial);

public sealed record ToolCallCompletion(JsonObject Response, string Status, string? ReasonCode, Guid? JournalId, PendingEvent? ExtraEvent);

public sealed record AgentMerchant(Guid AccountId, string Key, string Label);

public sealed record AgentView(Guid CashAccountId, Guid? SavingsAccountId, IReadOnlyList<AgentMerchant> Merchants,
    IReadOnlyList<ObligationResult> Obligations);

public static class AgentGatewayRules
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    public const string BudgetExhaustedReason = "Action budget exhausted.";

    public static int MaxActions(JsonObject snapshot) => (int)snapshot["maxActions"]!.GetValue<long>();

    public static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.BudgetExceeded;

    // Stable order for both ledgers: due day, then invoice reference.
    public static IReadOnlyList<ObligationResult> Order(IEnumerable<ObligationResult> obligations) =>
        obligations.OrderBy(o => o.DueDay ?? int.MaxValue).ThenBy(o => o.InvoiceReference, StringComparer.Ordinal).ToArray();
}
