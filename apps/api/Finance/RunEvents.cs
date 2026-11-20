using System.Text;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0007 finance audit events. EventHash is the PostgreSQL chain hash (hex); null in InMemoryLedger.
public sealed record RunEvent(
    Guid RunId, long Sequence, string EventType, int EventVersion, DateTimeOffset OccurredAt, string Source,
    string? Operation, string? Outcome, string? ReasonCode, string? IdempotencyKey,
    Guid? JournalId, Guid? ObligationId, JsonObject Payload, string? EventHash);

// An event built by an operation before the database assigns its sequence and hash.
internal sealed record PendingEvent(
    string EventType, JsonObject Payload, string Source = FinanceEvents.InternalApiSource,
    string? Operation = null, string? Outcome = null, string? ReasonCode = null, string? IdempotencyKey = null,
    Guid? JournalId = null, Guid? ObligationId = null);

// Single source of event types and payloads so both ledgers record identical events.
internal static class FinanceEvents
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
    }

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
