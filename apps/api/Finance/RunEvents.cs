using System.Text;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0007 finance audit events. EventHash is the PostgreSQL chain hash (hex); null in InMemoryLedger.
public sealed record RunEvent(
    Guid RunId, long Sequence, string EventType, int EventVersion, DateTimeOffset OccurredAt, string Source,
    string? Operation, string? Outcome, string? ReasonCode, string? IdempotencyKey,
    Guid? JournalId, Guid? ObligationId, JsonObject Payload, string? EventHash, DateTimeOffset? VirtualTime = null,
    string? TraceId = null);

// An event built by an operation before the database assigns its sequence and hash.
public sealed record PendingEvent(
    string EventType, JsonObject Payload, string Source = FinanceEvents.InternalApiSource,
    string? Operation = null, string? Outcome = null, string? ReasonCode = null, string? IdempotencyKey = null,
    Guid? JournalId = null, Guid? ObligationId = null, int EventVersion = FinanceEvents.Version, string? TraceId = null)
{
    // ADR-0009: inside an agent tool call, every event carries the call's source and trace ID.
    public PendingEvent WithCallContext() =>
        FinanceCallContext.Current is { } context && TraceId is null ? this with { Source = context.Source, TraceId = context.TraceId } : this;
}

// Ambient context for ledger calls made on behalf of an agent tool call.
public sealed record FinanceCallContext(string Source, string TraceId)
{
    private static readonly AsyncLocal<FinanceCallContext?> Ambient = new();
    public static FinanceCallContext? Current => Ambient.Value;

    public static IDisposable Begin(string source, string traceId)
    {
        var previous = Ambient.Value;
        Ambient.Value = new FinanceCallContext(source, traceId);
        return new Restore(previous);
    }

    private sealed class Restore(FinanceCallContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

// Single source of event types and payloads so both ledgers record identical events.
public static class FinanceEvents
{
    public const int Version = 1;
    public const string InternalApiSource = "internal_api";
    public const string ScenarioEngineSource = "scenario_engine";

    public static class Operations
    {
        public const string OpenRun = "open_run";
        public const string CreateAccount = "create_account";
        public const string CreateObligation = "create_obligation";
        public const string Transfer = "transfer";
        public const string MoveToSavings = "move_to_savings";
        public const string PayObligation = "pay_obligation";
        public const string ReverseJournal = "reverse_journal";
        public const string Commit = "commit";
        public const string CreateRun = "create_run";
        public const string TransitionRun = "transition_run";
        public const string ActivateRun = "activate_run";
        public const string AdvanceClock = "advance_clock";
        public const string ToolCall = "tool_call";
        public const string ClaimRun = "claim_run";
        public const string ReserveModelCall = "reserve_model_call";
        public const string ExpireSession = "expire_session";
    }

    public const string AgentSource = "agent";
    public const string WorkerSource = "worker";
    public const string GatewaySource = "gateway";

    // ADR-0009 tool-call events. Arguments and agent text are stored redacted.
    public static PendingEvent ToolCallProposed(string toolCallId, string tool, JsonObject redactedArguments) =>
        new("TOOL_CALL_PROPOSED", new JsonObject
        {
            ["toolCallId"] = toolCallId, ["tool"] = tool, ["arguments"] = redactedArguments.DeepClone()
        }, AgentSource, Operations.ToolCall, TraceId: toolCallId);

    public static PendingEvent ToolReturned(string toolCallId, string tool, string status, string? reasonCode, Guid? journalId) =>
        new("TOOL_RETURNED", new JsonObject
        {
            ["toolCallId"] = toolCallId, ["tool"] = tool, ["status"] = status, ["reasonCode"] = reasonCode, ["journalId"] = journalId
        }, AgentSource, Operations.ToolCall, status, reasonCode, TraceId: toolCallId);

    // ADR-0011: one event per agent turn; trace ID matches the turn's tool call ("turn-NNNN").
    // Version 2 (ADR-0012) adds model, stop reason, cache usage, tool-use count, and server-computed cost.
    public static PendingEvent ModelInvocationCompleted(ModelInvocation invocation, long costMicroUsd) =>
        new("MODEL_INVOCATION_COMPLETED", new JsonObject
        {
            ["turn"] = invocation.Turn, ["inputTokens"] = invocation.InputTokens, ["outputTokens"] = invocation.OutputTokens,
            ["latencyMs"] = invocation.LatencyMs, ["outputText"] = invocation.OutputText,
            ["model"] = invocation.Model, ["stopReason"] = invocation.StopReason, ["refusalCategory"] = invocation.RefusalCategory,
            ["cacheReadInputTokens"] = invocation.CacheReadInputTokens, ["cacheCreationInputTokens"] = invocation.CacheCreationInputTokens,
            ["toolUseCount"] = invocation.ToolUseCount, ["costMicroUsd"] = costMicroUsd
        }, AgentSource, Operations.ToolCall, TraceId: TurnTrace(invocation.Turn), EventVersion: 2);

    // ADR-0013 A1.
    public static PendingEvent ModelCostReserved(ModelReservation request, long maxOutputTokens, long reservedMicroUsd) =>
        new("MODEL_COST_RESERVED", new JsonObject
        {
            ["turn"] = request.Turn, ["attempt"] = request.Attempt, ["inputTokens"] = request.InputTokens,
            ["maxOutputTokens"] = maxOutputTokens, ["reservedMicroUsd"] = reservedMicroUsd
        }, GatewaySource, Operations.ReserveModelCall, TraceId: TurnTrace(request.Turn));

    public static PendingEvent RunReclaimed() =>
        new("RUN_RECLAIMED", new JsonObject { ["reason"] = "Worker session expired before initialization." },
            WorkerSource, Operations.ClaimRun);

    public static string TurnTrace(int turn) => $"turn-{turn:D4}";

    public static PendingEvent AgentEvent(string eventType, string toolCallId, JsonObject payload) =>
        new(eventType, payload, AgentSource, Operations.ToolCall, TraceId: toolCallId);

    // ADR-0010 scenario and clock events.
    public static PendingEvent ScenarioAccountCreated(Guid accountId, ScenarioMerchant merchant) =>
        new("ACCOUNT_CREATED", new JsonObject
        {
            ["accountId"] = accountId, ["kind"] = Wire(AccountKind.Merchant),
            ["scenarioKey"] = merchant.Key, ["label"] = merchant.Label
        }, Operation: Operations.ActivateRun);

    public static PendingEvent ScenarioMaterialized(ScenarioDefinition scenario, int eventCount, string scheduleHash) =>
        new("SCENARIO_MATERIALIZED", new JsonObject
        {
            ["scenarioVersion"] = scenario.VersionLabel, ["scenarioHash"] = scenario.FileHash,
            ["prng"] = Xoshiro256StarStar.Algorithm, ["eventCount"] = eventCount, ["scheduleHash"] = scheduleHash
        }, Operation: Operations.ActivateRun);

    public static PendingEvent ScenarioEventDelivered(string operation, MaterializedScenarioEvent evt, int deliveredAtDay, Guid? obligationId, string source) =>
        new("SCENARIO_EVENT_DELIVERED", new JsonObject
        {
            ["eventKey"] = evt.Key, ["kind"] = evt.Kind, ["scheduledDay"] = evt.Day,
            ["deliveredAtDay"] = deliveredAtDay, ["obligationId"] = obligationId
        }, source, operation, ObligationId: obligationId);

    public static PendingEvent ClockAdvanced(int fromDay, int toDay, int requestedDays, string source) =>
        new("CLOCK_ADVANCED", new JsonObject
        {
            ["fromDay"] = fromDay, ["toDay"] = toDay, ["requestedDays"] = requestedDays
        }, source, Operations.AdvanceClock);

    public static PendingEvent ScenarioObligationCreated(ObligationResult obligation, string operation, string eventKey, string source) =>
        new("OBLIGATION_CREATED", new JsonObject
        {
            ["obligationId"] = obligation.ObligationId,
            ["merchantAccountId"] = obligation.MerchantAccountId,
            ["amountSen"] = obligation.AmountSen,
            ["invoiceReference"] = obligation.InvoiceReference,
            ["dueDay"] = obligation.DueDay,
            ["label"] = obligation.Label,
            ["scenarioEventKey"] = eventKey
        }, source, operation, ObligationId: obligation.ObligationId);

    // ADR-0008: RUN_CREATED v2 for managed runs carries the configuration hash, not the full snapshot.
    public static PendingEvent ManagedRunCreated(JsonObject snapshot, string configHash) =>
        new("RUN_CREATED", new JsonObject
        {
            ["configHash"] = configHash,
            ["configSchemaVersion"] = snapshot["configSchemaVersion"]!.DeepClone(),
            ["scenarioId"] = snapshot["scenarioId"]!.DeepClone(),
            ["seed"] = snapshot["seed"]!.DeepClone(),
            ["transactionLimitSen"] = snapshot["transactionLimitSen"]!.DeepClone(),
            ["startingBalanceSen"] = snapshot["startingBalanceSen"]!.DeepClone()
        }, Operation: Operations.CreateRun, EventVersion: 2);

    public static PendingEvent RunStatusChanged(string operation, RunStatus from, RunStatus to, string? reason, string source) =>
        new("RUN_STATUS_CHANGED", new JsonObject
        {
            ["from"] = RunLifecycle.Wire(from),
            ["to"] = RunLifecycle.Wire(to),
            ["reason"] = reason
        }, source, operation);

    public static PendingEvent RunCreated(long transactionLimitSen) =>
        new("RUN_CREATED", new JsonObject { ["transactionLimitSen"] = transactionLimitSen }, Operation: Operations.OpenRun);

    public static PendingEvent AccountCreated(string operation, Guid accountId, AccountKind kind) =>
        new("ACCOUNT_CREATED", new JsonObject { ["accountId"] = accountId, ["kind"] = Wire(kind) }, Operation: operation);

    public static PendingEvent ObligationCreated(ObligationResult obligation) =>
        new("OBLIGATION_CREATED", new JsonObject
        {
            ["obligationId"] = obligation.ObligationId,
            ["merchantAccountId"] = obligation.MerchantAccountId,
            ["amountSen"] = obligation.AmountSen,
            ["invoiceReference"] = obligation.InvoiceReference
        }, Operation: Operations.CreateObligation, ObligationId: obligation.ObligationId);

    public static PendingEvent LedgerCommitted(string operation, LedgerJournal journal, string source = InternalApiSource) =>
        new("LEDGER_COMMITTED", new JsonObject
        {
            ["journalId"] = journal.JournalId,
            ["kind"] = Wire(journal.Kind),
            ["postings"] = new JsonArray(journal.Postings
                .Select(posting => (JsonNode)new JsonObject { ["accountId"] = posting.AccountId, ["amountSen"] = posting.AmountSen })
                .ToArray()),
            ["obligationId"] = journal.ObligationId,
            ["reversesJournalId"] = journal.ReversesJournalId
        }, source, operation, LedgerReasonCodes.ToWireValue(LedgerOutcome.Committed), null, journal.IdempotencyKey,
            journal.JournalId, journal.ObligationId);

    public static PendingEvent ObligationPaid(Guid obligationId, Guid journalId, string idempotencyKey) =>
        new("OBLIGATION_PAID", new JsonObject { ["obligationId"] = obligationId, ["journalId"] = journalId },
            Operation: Operations.PayObligation, IdempotencyKey: idempotencyKey, JournalId: journalId, ObligationId: obligationId);

    public static PendingEvent JournalReversed(LedgerJournal reversal) =>
        new("JOURNAL_REVERSED", new JsonObject
        {
            ["originalJournalId"] = reversal.ReversesJournalId,
            ["reversalJournalId"] = reversal.JournalId,
            ["reason"] = reversal.ReversalReason,
            ["initiator"] = reversal.Initiator is { } initiator ? Wire(initiator) : null
        }, SourceFor(reversal.Initiator), Operations.ReverseJournal, IdempotencyKey: reversal.IdempotencyKey,
            JournalId: reversal.JournalId, ObligationId: reversal.ObligationId);

    public static PendingEvent ObligationPaymentReversed(LedgerJournal reversal) =>
        new("OBLIGATION_PAYMENT_REVERSED", new JsonObject
        {
            ["obligationId"] = reversal.ObligationId,
            ["originalJournalId"] = reversal.ReversesJournalId,
            ["reversalJournalId"] = reversal.JournalId
        }, SourceFor(reversal.Initiator), Operations.ReverseJournal, IdempotencyKey: reversal.IdempotencyKey,
            JournalId: reversal.JournalId, ObligationId: reversal.ObligationId);

    public static PendingEvent LedgerReplayed(string operation, CommitResult result, string idempotencyKey, string source = InternalApiSource) =>
        new("LEDGER_REPLAYED", new JsonObject
        {
            ["journalId"] = result.JournalId,
            ["effective"] = result.Effective,
            ["reversedByJournalId"] = result.ReversedByJournalId
        }, source, operation, LedgerReasonCodes.ToWireValue(LedgerOutcome.Replayed), null, idempotencyKey, result.JournalId);

    // Rejected request IDs stay in the payload only: they may not exist, so they are not foreign keys.
    public static PendingEvent PolicyDenied(string operation, LedgerException exception, JsonObject request, string source) =>
        new("POLICY_DENIED", new JsonObject
        {
            ["operation"] = operation,
            ["outcome"] = LedgerReasonCodes.ToWireValue(exception.Outcome),
            ["reasonCode"] = LedgerReasonCodes.ToWireValue(exception.ReasonCode),
            ["request"] = request
        }, source, operation, LedgerReasonCodes.ToWireValue(exception.Outcome),
            LedgerReasonCodes.ToWireValue(exception.ReasonCode), request["idempotencyKey"]?.GetValue<string>());

    public static PendingEvent FinanceError(string operation, LedgerReasonCode code, string? idempotencyKey, string source) =>
        new("FINANCE_ERROR", new JsonObject
        {
            ["operation"] = operation,
            ["reasonCode"] = LedgerReasonCodes.ToWireValue(code)
        }, source, operation, LedgerReasonCodes.ToWireValue(LedgerOutcome.InternalError),
            LedgerReasonCodes.ToWireValue(code), idempotencyKey);

    public static string SourceFor(ReversalInitiator? initiator) =>
        initiator == ReversalInitiator.ScenarioEngine ? ScenarioEngineSource : InternalApiSource;

    // ADR-0007: an internal failure is an error, whatever exception type carried it; it is not a denial.
    public static PendingEvent Rejection(string operation, LedgerException exception, JsonObject request, string source) =>
        exception.Outcome == LedgerOutcome.InternalError
            ? FinanceError(operation, exception.ReasonCode, request["idempotencyKey"]?.GetValue<string>(), source)
            : PolicyDenied(operation, exception, request, source);

    // Records a rejection only when the run is recordable (ADR-0007 "Unrecordable requests").
    public static bool IsRecordable(Guid runId, LedgerException exception) =>
        runId != Guid.Empty && exception.ReasonCode != LedgerReasonCode.RunNotFound;

    public static JsonObject Request(params (string Key, object? Value)[] fields)
    {
        var request = new JsonObject();
        foreach (var (key, value) in fields)
        {
            request[key] = value switch
            {
                null => null,
                Guid guid => JsonValue.Create(guid),
                long number => JsonValue.Create(number),
                string text => JsonValue.Create(text),
                Enum enumValue => JsonValue.Create(Wire(enumValue)),
                _ => throw new ArgumentException($"Unsupported event request value for {key}.")
            };
        }
        return request;
    }

    // PascalCase enum names to the snake_case values used in the database.
    public static string Wire(Enum value)
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[index]));
        }
        return builder.ToString();
    }
}
