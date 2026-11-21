using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

public enum AccountKind { SimulatorTreasury, AgentCash, AgentSavings, Merchant }
public enum JournalKind { OpeningFunding, BillPayment, Transfer, SavingsMove, Reversal }
public sealed record PostingRequest(Guid AccountId, long AmountSen);
// Server-side origin of a reversal (ADR-0006). Never taken from request bodies or model output.
public enum ReversalInitiator { Researcher, ScenarioEngine }
public sealed record JournalRequest(Guid RunId, string IdempotencyKey, JournalKind Kind, string Description, IReadOnlyList<PostingRequest> Postings, Guid? ObligationId = null);
public sealed record LedgerPosting(Guid AccountId, long AmountSen);
public sealed record LedgerJournal(
    Guid JournalId, Guid RunId, string IdempotencyKey, JournalKind Kind, string Description, IReadOnlyList<LedgerPosting> Postings,
    Guid? ObligationId = null, Guid? ReversesJournalId = null, string? ReversalReason = null, ReversalInitiator? Initiator = null);
// Committed: the journal was durably recorded. Effective: it has not been reversed (ADR-0006).
public sealed record CommitResult(Guid JournalId, bool Committed, bool WasIdempotentReplay, bool Effective = true, Guid? ReversedByJournalId = null);
public sealed record OpenRunResult(Guid JournalId, bool Committed, bool WasIdempotentReplay, Guid CashAccountId);
public sealed record ObligationResult(Guid ObligationId, Guid MerchantAccountId, long AmountSen, bool Paid, string InvoiceReference,
    int? DueDay = null, string? Label = null);
public abstract class LedgerException(LedgerReasonCode reasonCode, string message) : Exception(message)
{
    public LedgerReasonCode ReasonCode { get; } = reasonCode;
    public LedgerOutcome Outcome => LedgerReasonCodes.OutcomeOf(ReasonCode);
}
public sealed class LedgerValidationException(LedgerReasonCode reasonCode, string message) : LedgerException(reasonCode, message);
public sealed class IdempotencyConflictException(LedgerReasonCode reasonCode, string message) : LedgerException(reasonCode, message);

public interface IFinanceLedger
{
    Guid CreateAccount(Guid runId, AccountKind kind);
    OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen);
    CommitResult Transfer(Guid runId, string idempotencyKey, Guid sourceAccountId, Guid destinationAccountId, long amountSen);
    CommitResult MoveToSavings(Guid runId, string idempotencyKey, Guid cashAccountId, Guid savingsAccountId, long amountSen);
    ObligationResult CreateObligation(Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference);
    ObligationResult GetObligation(Guid runId, Guid obligationId);
    CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId);
    CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator);
    long GetBalance(Guid runId, Guid accountId);
    IReadOnlyList<LedgerJournal> GetJournals(Guid runId);
    IReadOnlyList<RunEvent> GetEvents(Guid runId, long afterSequence, int limit);
    RunInfo CreateManagedRun(JsonObject? config, string? codeRevision);
    RunInfo GetRun(Guid runId);
    RunInfo TransitionRun(Guid runId, RunStatus to, string? reason, string source);
    OpenRunResult ActivateRun(Guid runId, string source);
    ClockResult AdvanceClock(Guid runId, int days, string source);
    EnvironmentView GetEnvironment(Guid runId);

    // ADR-0009 gateway support. Token hashes are SHA-256 of the session token; tokens are never stored.
    RunInfo? ClaimQueuedRun(byte[] tokenHash, DateTimeOffset expiresAt);
    Guid? ResolveSession(byte[] tokenHash, DateTimeOffset now, DateTimeOffset newExpiresAt);
    ToolCallBegin BeginToolCall(Guid runId, string toolCallId, string tool, JsonObject redactedArguments, string requestHash);
    void CompleteToolCall(Guid runId, string toolCallId, string tool, ToolCallCompletion completion);
    AgentView GetAgentView(Guid runId);
    ModelInvocationResult RecordModelInvocation(Guid runId, int turn, long inputTokens, long outputTokens, long latencyMs, string outputText);
}

public static class RunEventPaging
{
    public const int MaxLimit = 500;
}

public static class InvoiceReferences {
    public static void Validate(string? reference) {
        if (string.IsNullOrWhiteSpace(reference))
            throw new LedgerValidationException(
                LedgerReasonCode.InvalidInvoiceReference,
                "Invoice reference is required.");

        if (!string.Equals(
                reference, reference.Trim(), StringComparison.Ordinal))
            throw new LedgerValidationException(
                LedgerReasonCode.InvalidInvoiceReference,
                "Invoice reference must not have surrounding whitespace.");

        if (reference.StartsWith("legacy:", StringComparison.Ordinal))
            throw new LedgerValidationException(
                LedgerReasonCode.InvalidInvoiceReference,
                "The legacy: invoice reference prefix is reserved.");
    }
}

// Validation shared by both ledgers so the same request yields the same reason code.
internal static class LedgerRequestValidation
{
    public static void ValidateJournalFields(JournalRequest request)
    {
        if (request.RunId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(request.Description))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run, idempotency key, and description are required.");
        if (request.Postings is null || request.Postings.Count != 2)
            throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "A journal requires exactly two postings.");
        if (request.Postings.Any(posting => posting.AccountId == Guid.Empty))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Postings require account IDs.");
        if (request.Postings.Any(posting => posting.AmountSen == 0))
            throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Zero-value postings are not allowed.");
        if (checked(request.Postings[0].AmountSen + request.Postings[1].AmountSen) != 0)
            throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Journal postings must balance to zero.");
        if (request.Postings[0].AccountId == request.Postings[1].AccountId)
            throw new LedgerValidationException(LedgerReasonCode.SameSourceAndDestination, "Journal postings must use distinct accounts.");
        if (request.Kind == JournalKind.Reversal)
            throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Reversals must use ReverseJournal.");
        if ((request.Kind == JournalKind.BillPayment) != (request.ObligationId is not null))
            throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Bill payments, and only bill payments, must bind to an obligation.");
    }

    // Checked arithmetic overflow is a malformed amount, recorded like any other rejection.
    public static T TranslateOverflow<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (OverflowException)
        {
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Amount exceeds supported range.");
        }
    }

    public static void ValidateReversal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || originalJournalId == Guid.Empty || string.IsNullOrWhiteSpace(reason))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID, idempotency key, original journal ID, and reason are required.");
        if (!Enum.IsDefined(initiator))
            throw new LedgerValidationException(LedgerReasonCode.UnsupportedInitiatorType, "Unsupported reversal initiator.");
    }

    // ADR-0006: full reversal of agent money movements only.
    public static void EnsureReversible(LedgerJournal original)
    {
        if (original.Kind is not (JournalKind.BillPayment or JournalKind.Transfer or JournalKind.SavingsMove))
            throw new LedgerValidationException(LedgerReasonCode.JournalNotReversible, $"{original.Kind} journals cannot be reversed.");
        if (original.Kind == JournalKind.BillPayment && original.ObligationId is null)
            throw new LedgerValidationException(LedgerReasonCode.JournalNotReversible, "Legacy bill payments without an obligation link cannot be reversed.");
    }

    public static void EnsureReversalReplay(LedgerJournal existing, Guid originalJournalId, string reason)
    {
        if (existing.Kind != JournalKind.Reversal || existing.ReversesJournalId != originalJournalId || existing.ReversalReason != reason)
            throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used with a different payload.");
    }

    public static void ValidateAccountKinds(JournalKind kind, AccountKind debitKind, AccountKind creditKind)
    {
        var valid = kind switch
        {
            JournalKind.OpeningFunding => debitKind == AccountKind.SimulatorTreasury && creditKind == AccountKind.AgentCash,
            JournalKind.Transfer => debitKind is AccountKind.AgentCash or AccountKind.AgentSavings && creditKind is AccountKind.Merchant or AccountKind.AgentCash or AccountKind.AgentSavings,
            JournalKind.SavingsMove => debitKind == AccountKind.AgentCash && creditKind == AccountKind.AgentSavings,
            JournalKind.BillPayment => debitKind == AccountKind.AgentCash && creditKind == AccountKind.Merchant,
            _ => false
        };

        if (!valid)
            throw new LedgerValidationException(LedgerReasonCode.AccountKindNotPermitted, "Posting account kinds are not permitted for this journal kind.");
    }

    public static void ValidateAmount(long amountSen)
    {
        if (amountSen <= 0)
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Amount must be positive.");
    }

    public static void ValidateObligation(Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference)
    {
        if (runId == Guid.Empty || merchantAccountId == Guid.Empty)
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID and merchant account ID are required.");
        if (amountSen <= 0)
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Obligation amount must be positive sen.");
        InvoiceReferences.Validate(invoiceReference);
    }
}

public sealed class InMemoryLedger : IFinanceLedger
{
    private readonly TimeProvider _time;

    public InMemoryLedger() : this(TimeProvider.System) { }

    // Tests pass a controllable clock for wall-time budgets and timestamps.
    public InMemoryLedger(TimeProvider time) => _time = time;

    private sealed record Account(Guid RunId, AccountKind Kind, string? ScenarioKey = null, string? Label = null);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, long> _balances = [];
    private readonly Dictionary<(Guid RunId, string Key), LedgerJournal> _journalsByKey = [];
    private readonly List<LedgerJournal> _journals = [];
    private readonly Dictionary<Guid, (Guid RunId, Guid MerchantAccountId, long AmountSen, bool Paid, string InvoiceReference, Guid? PaidEntryId, int? DueDay, string? Label)> _obligations = [];
    private readonly Dictionary<Guid, LedgerJournal> _journalsById = [];
    private readonly Dictionary<Guid, Guid> _reversalIdsByOriginal = [];
    private readonly Dictionary<(Guid RunId, string InvoiceReference), Guid> _obligationIdsByInvoice = [];
    private readonly Dictionary<Guid, long> _runTransactionLimits = [];
    private readonly Dictionary<Guid, List<RunEvent>> _events = [];
    private List<PendingEvent>? _pending;
    private readonly Dictionary<Guid, ManagedRun> _managedRuns = [];
    private readonly Dictionary<string, (Guid RunId, DateTimeOffset ExpiresAt, bool Revoked)> _sessions = [];
    private readonly Dictionary<(Guid RunId, string ToolCallId), ToolCallRecord> _toolCalls = [];

    private sealed class ToolCallRecord(string tool, JsonObject arguments, string requestHash)
    {
        public string Tool { get; } = tool;
        public JsonObject Arguments { get; } = arguments;
        public string RequestHash { get; } = requestHash;
        public JsonObject? Response;
        public string? Outcome, ReasonCode;
        public Guid? JournalId;
    }

    private sealed class ManagedRun(JsonObject snapshot, string hash)
    {
        public RunStatus Status = RunStatus.Draft;
        public string? Reason;
        public JsonObject Snapshot { get; } = snapshot;
        public string Hash { get; } = hash;
        public DateTimeOffset? QueuedAt, StartedAt, FinishedAt;
        public DateOnly? VirtualStart;
        public int? VirtualDay;
        public readonly List<ScheduledEvent> Schedule = [];
        public long TokensUsed;
    }

    private sealed class ScheduledEvent(MaterializedScenarioEvent evt)
    {
        public MaterializedScenarioEvent Event { get; } = evt;
        public int? DeliveredAt;
    }

    private RunStatus? StatusOf(Guid runId) => _managedRuns.TryGetValue(runId, out var run) ? run.Status : null;

    public Guid CreateAccount(Guid runId, AccountKind kind) =>
        Record(FinanceEvents.Operations.CreateAccount, runId, FinanceEvents.Request(("kind", kind)),
            FinanceEvents.InternalApiSource, () => CreateAccountCore(runId, kind, FinanceEvents.Operations.CreateAccount));

    private Guid CreateAccountCore(Guid runId, AccountKind kind, string operation)
    {
        if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
        if (!Enum.IsDefined(kind)) throw new LedgerValidationException(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.");
        RunLifecycle.EnsureActive(StatusOf(runId), RunLifecycle.Setup);
        var id = Guid.NewGuid();
        _accounts.Add(id, new Account(runId, kind));
        _balances.Add(id, 0);
        Emit(FinanceEvents.AccountCreated(operation, id, kind));
        return id;
    }

    public OpenRunResult OpenRun(
        Guid runId,
        string idempotencyKey,
        long openingBalanceSen,
        long transactionLimitSen) =>
        Record(FinanceEvents.Operations.OpenRun, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("openingBalanceSen", openingBalanceSen), ("transactionLimitSen", transactionLimitSen)),
            FinanceEvents.InternalApiSource, () =>
            {
                ValidateOpenRun(runId, idempotencyKey, openingBalanceSen, transactionLimitSen);
                // ADR-0008: managed runs are funded only by activation, from their snapshot.
                if (_managedRuns.ContainsKey(runId))
                    throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Managed runs are funded only by activation.");
                return OpenRunCore(runId, idempotencyKey, openingBalanceSen, transactionLimitSen, FinanceEvents.Operations.OpenRun);
            });

    private static void ValidateOpenRun(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID and idempotency key are required.");
        if (openingBalanceSen <= 0 || transactionLimitSen <= 0)
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Opening balance and transaction limit must be positive sen.");
    }

    private OpenRunResult OpenRunCore(
        Guid runId,
        string idempotencyKey,
        long openingBalanceSen,
        long transactionLimitSen,
        string operation)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new LedgerValidationException(
                LedgerReasonCode.MissingRequiredField,
                "Run ID and idempotency key are required.");

        if (openingBalanceSen <= 0 || transactionLimitSen <= 0)
            throw new LedgerValidationException(
                LedgerReasonCode.InvalidAmount,
                "Opening balance and transaction limit must be positive sen.");

        lock (_gate)
        {
            var hasPolicy = _runTransactionLimits.TryGetValue(
                runId, out var configuredLimit);

            if (hasPolicy && configuredLimit != transactionLimitSen)
                throw new IdempotencyConflictException(
                    LedgerReasonCode.RunPolicyConflict,
                    "Run already has a different transaction policy.");

            var existing = _journals.FirstOrDefault(
                journal => journal.RunId == runId
                    && journal.Kind == JournalKind.OpeningFunding);

            if (existing is not null)
            {
                if (!hasPolicy)
                    throw new LedgerValidationException(
                        LedgerReasonCode.RunPolicyNotConfigured,
                        "Existing run has ledger activity but no transaction policy.");

                var cashPosting = existing.Postings.Single(
                    posting => _accounts[posting.AccountId].Kind
                        == AccountKind.AgentCash);

                if (existing.IdempotencyKey != idempotencyKey
                    || cashPosting.AmountSen != openingBalanceSen)
                {
                    throw new IdempotencyConflictException(
                        LedgerReasonCode.RunPolicyConflict,
                        "Run has already been initialized with a different opening allocation.");
                }

                Emit(FinanceEvents.LedgerReplayed(operation,
                    new CommitResult(existing.JournalId, true, true), idempotencyKey));
                return new OpenRunResult(
                    existing.JournalId, true, true, cashPosting.AccountId);
            }

            if (_journals.Any(journal => journal.RunId == runId))
                throw new LedgerValidationException(
                    LedgerReasonCode.RunAlreadyFunded,
                    "Run already has ledger activity and cannot receive new opening funds.");

            if (!hasPolicy)
                Emit(FinanceEvents.RunCreated(transactionLimitSen));
            var treasuryId = CreateAccountCore(
                runId, AccountKind.SimulatorTreasury, operation);
            var cashId = CreateAccountCore(runId, AccountKind.AgentCash, operation);

            _runTransactionLimits[runId] = transactionLimitSen;

            CommitResult result;

            try
            {
                result = CommitCore(new JournalRequest(
                    runId,
                    idempotencyKey,
                    JournalKind.OpeningFunding,
                    "authorized virtual opening allocation",
                    [
                        new(treasuryId, checked(-openingBalanceSen)),
                        new(cashId, openingBalanceSen)
                    ]), operation, activation: true);
            }
            catch
            {
                _accounts.Remove(treasuryId);
                _accounts.Remove(cashId);
                _balances.Remove(treasuryId);
                _balances.Remove(cashId);

                if (!hasPolicy)
                    _runTransactionLimits.Remove(runId);

                throw;
            }

            return new OpenRunResult(
                result.JournalId,
                result.Committed,
                result.WasIdempotentReplay,
                cashId);
        }
    }

    public CommitResult Commit(JournalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Record(FinanceEvents.Operations.Commit, request.RunId,
            FinanceEvents.Request(("idempotencyKey", request.IdempotencyKey), ("kind", request.Kind)),
            FinanceEvents.InternalApiSource, () => CommitCore(request, FinanceEvents.Operations.Commit));
    }

    private CommitResult CommitCore(JournalRequest request, string operation, bool activation = false)
    {
        {
            // Check order mirrors PostgresLedger so both return the same reason code.
            ValidateRequestFields(request);
            if (!activation) RunLifecycle.EnsureActive(StatusOf(request.RunId), RunLifecycle.MoneyMovement);

            if (!_runTransactionLimits.TryGetValue(
                    request.RunId, out var transactionLimitSen))
            {
                throw new LedgerValidationException(
                    LedgerReasonCode.RunPolicyNotConfigured,
                    "Run transaction policy is not configured.");
            }

            var key = (request.RunId, request.IdempotencyKey);
            var fingerprint = Fingerprint(request);

            if (_journalsByKey.TryGetValue(key, out var existing))
            {
                if (Fingerprint(existing) != fingerprint)
                    throw new IdempotencyConflictException(
                        LedgerReasonCode.IdempotencyKeyConflict,
                        "Idempotency key was previously used with a different payload.");

                var replay = ReplayResult(existing);
                Emit(FinanceEvents.LedgerReplayed(operation, replay, request.IdempotencyKey));
                return replay;
            }

            ValidateAccountsAndShape(request);

            if (request.Kind == JournalKind.OpeningFunding)
            {
                if (_journals.Any(journal =>
                        journal.RunId == request.RunId
                        && journal.Kind == JournalKind.OpeningFunding))
                {
                    throw new LedgerValidationException(
                        LedgerReasonCode.RunAlreadyFunded,
                        "Run already has an opening funding journal.");
                }
            }
            else
            {
                var debit = request.Postings.Single(
                    posting => posting.AmountSen < 0);

                if (checked(-debit.AmountSen) > transactionLimitSen)
                    throw new LedgerValidationException(
                        LedgerReasonCode.ExceedsTransactionLimit,
                        "Run transaction limit exceeded.");
            }

            var deltas = request.Postings
                .GroupBy(posting => posting.AccountId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(posting => posting.AmountSen));

            foreach (var (accountId, delta) in deltas)
            {
                var next = checked(_balances[accountId] + delta);

                if (_accounts[accountId].Kind
                        is AccountKind.AgentCash or AccountKind.AgentSavings
                    && next < 0)
                {
                    throw new LedgerValidationException(
                        LedgerReasonCode.InsufficientFunds,
                        "Agent account cannot be overdrawn.");
                }
            }

            var journal = new LedgerJournal(
                Guid.NewGuid(),
                request.RunId,
                request.IdempotencyKey,
                request.Kind,
                request.Description,
                request.Postings.Select(posting =>
                    new LedgerPosting(
                        posting.AccountId, posting.AmountSen)).ToArray(),
                request.ObligationId);

            foreach (var (accountId, delta) in deltas)
                _balances[accountId] = checked(_balances[accountId] + delta);

            Append(journal);
            Emit(FinanceEvents.LedgerCommitted(operation, journal));

            return new CommitResult(journal.JournalId, true, false);
        }
    }

    public bool TrySpend(Guid runId, string idempotencyKey, Guid sourceAccountId, Guid destinationAccountId, long amountSen)
    {
        if (amountSen <= 0) return false;
        try { Commit(new JournalRequest(runId, idempotencyKey, JournalKind.Transfer, "virtual transfer", [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)])); return true; }
        catch (LedgerValidationException) { return false; }
    }

    public CommitResult Transfer(
        Guid runId,
        string idempotencyKey,
        Guid sourceAccountId,
        Guid destinationAccountId,
        long amountSen) =>
        Record(FinanceEvents.Operations.Transfer, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("sourceAccountId", sourceAccountId),
                ("destinationAccountId", destinationAccountId), ("amountSen", amountSen)),
            FinanceEvents.InternalApiSource, () =>
            {
                ValidateAmount(amountSen);
                return CommitCore(new JournalRequest(
                    runId,
                    idempotencyKey,
                    JournalKind.Transfer,
                    "virtual transfer",
                    [
                        new(sourceAccountId, checked(-amountSen)),
                        new(destinationAccountId, amountSen)
                    ]), FinanceEvents.Operations.Transfer);
            });

    public CommitResult MoveToSavings(
        Guid runId,
        string idempotencyKey,
        Guid cashAccountId,
        Guid savingsAccountId,
        long amountSen) =>
        Record(FinanceEvents.Operations.MoveToSavings, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("cashAccountId", cashAccountId),
                ("savingsAccountId", savingsAccountId), ("amountSen", amountSen)),
            FinanceEvents.InternalApiSource, () =>
            {
                ValidateAmount(amountSen);
                return CommitCore(new JournalRequest(
                    runId,
                    idempotencyKey,
                    JournalKind.SavingsMove,
                    "move to savings",
                    [
                        new(cashAccountId, checked(-amountSen)),
                        new(savingsAccountId, amountSen)
                    ]), FinanceEvents.Operations.MoveToSavings);
            });


    public ObligationResult CreateObligation(
        Guid runId,
        Guid merchantAccountId,
        long amountSen,
        string invoiceReference) =>
        Record(FinanceEvents.Operations.CreateObligation, runId,
            FinanceEvents.Request(("merchantAccountId", merchantAccountId), ("amountSen", amountSen), ("invoiceReference", invoiceReference)),
            FinanceEvents.InternalApiSource, () => CreateObligationCore(runId, merchantAccountId, amountSen, invoiceReference));

    private ObligationResult CreateObligationCore(Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference)
    {
        ValidateObligationRequest(runId, merchantAccountId, amountSen, invoiceReference);
        RunLifecycle.EnsureActive(StatusOf(runId), RunLifecycle.Setup);
        var (obligation, created) = CreateObligationInternal(runId, merchantAccountId, amountSen, invoiceReference, null, null);
        if (created) Emit(FinanceEvents.ObligationCreated(obligation));
        return obligation;
    }

    // Mirrors PostgresLedger.CreateObligationWithinTransaction: returns an identical existing obligation or inserts one.
    private (ObligationResult Obligation, bool Created) CreateObligationInternal(
        Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference, int? dueDay, string? label)
    {
        {
            var invoiceKey = (runId, invoiceReference);

            if (_obligationIdsByInvoice.TryGetValue(
                    invoiceKey, out var existingId))
            {
                var existing = _obligations[existingId];

                if (existing.MerchantAccountId != merchantAccountId
                    || existing.AmountSen != amountSen)
                {
                    throw new IdempotencyConflictException(
                        LedgerReasonCode.InvoiceReferenceConflict,
                        "Invoice reference was previously used with a different payload.");
                }

                return (GetObligation(runId, existingId), false);
            }

            EnsureAccountKind(
                runId, merchantAccountId, AccountKind.Merchant);

            var obligationId = Guid.NewGuid();

            _obligations.Add(
                obligationId,
                (runId, merchantAccountId, amountSen,
                false, invoiceReference, null, dueDay, label));

            _obligationIdsByInvoice.Add(invoiceKey, obligationId);

            return (new ObligationResult(obligationId, merchantAccountId, amountSen, false, invoiceReference, dueDay, label), true);
        }
    }

    public ObligationResult GetObligation(
        Guid runId,
        Guid obligationId)
    {
        lock (_gate)
        {
            if (!_obligations.TryGetValue(
                    obligationId, out var obligation)
                || obligation.RunId != runId)
            {
                throw new LedgerValidationException(
                    LedgerReasonCode.ObligationNotFound,
                    "Obligation not found in this run.");
            }

            return new ObligationResult(
                obligationId,
                obligation.MerchantAccountId,
                obligation.AmountSen,
                obligation.Paid,
                obligation.InvoiceReference,
                obligation.DueDay,
                obligation.Label);
        }
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId) =>
        Record(FinanceEvents.Operations.PayObligation, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("cashAccountId", cashAccountId), ("obligationId", obligationId)),
            FinanceEvents.InternalApiSource, () => PayObligationCore(runId, idempotencyKey, cashAccountId, obligationId));

    private CommitResult PayObligationCore(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId)
    {
        RunLifecycle.EnsureActive(StatusOf(runId), RunLifecycle.MoneyMovement);
        {
            if (!_obligations.TryGetValue(obligationId, out var obligation) || obligation.RunId != runId)
                throw new LedgerValidationException(LedgerReasonCode.ObligationNotFound, "Obligation not found in this run.");

            var request = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, $"obligation:{obligationId:N}",
                [new(cashAccountId, checked(-obligation.AmountSen)), new(obligation.MerchantAccountId, obligation.AmountSen)], obligationId);
            ValidateRequestFields(request);

            // Permit retries of a payment already committed with this key, but never
            // allow a second key to pay the same obligation again.
            var paymentKey = (runId, idempotencyKey);
            if (_journalsByKey.TryGetValue(paymentKey, out var previous))
            {
                if (Fingerprint(previous) != Fingerprint(request))
                    throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used with a different payload.");
                var replay = ReplayResult(previous);
                Emit(FinanceEvents.LedgerReplayed(FinanceEvents.Operations.PayObligation, replay, idempotencyKey));
                return replay;
            }
            if (obligation.Paid) throw new LedgerValidationException(LedgerReasonCode.ObligationAlreadyPaid, "Obligation has already been paid.");
            // Account ownership and kind are checked by Commit, matching PostgresLedger.
            var result = CommitCore(request, FinanceEvents.Operations.PayObligation);
            _obligations[obligationId] = obligation with { Paid = true, PaidEntryId = result.JournalId };
            Emit(FinanceEvents.ObligationPaid(obligationId, result.JournalId, idempotencyKey));
            return result;
        }
    }

    // ADR-0006 full reversal. Check order mirrors PostgresLedger.ReverseJournal.
    public CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator) =>
        Record(FinanceEvents.Operations.ReverseJournal, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("originalJournalId", originalJournalId), ("reason", reason), ("initiator", initiator)),
            FinanceEvents.SourceFor(initiator), () => ReverseJournalCore(runId, idempotencyKey, originalJournalId, reason, initiator));

    private CommitResult ReverseJournalCore(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator)
    {
        LedgerRequestValidation.ValidateReversal(runId, idempotencyKey, originalJournalId, reason, initiator);
        RunLifecycle.EnsureActive(StatusOf(runId), RunLifecycle.MoneyMovement);

        {
            if (_journalsByKey.TryGetValue((runId, idempotencyKey), out var existing))
            {
                LedgerRequestValidation.EnsureReversalReplay(existing, originalJournalId, reason);
                var replay = ReplayResult(existing);
                Emit(FinanceEvents.LedgerReplayed(FinanceEvents.Operations.ReverseJournal, replay, idempotencyKey, FinanceEvents.SourceFor(initiator)));
                return replay;
            }

            if (!_journalsById.TryGetValue(originalJournalId, out var original) || original.RunId != runId)
                throw new LedgerValidationException(LedgerReasonCode.JournalNotFound, "Journal not found in this run.");
            LedgerRequestValidation.EnsureReversible(original);
            if (_reversalIdsByOriginal.ContainsKey(originalJournalId))
                throw new LedgerValidationException(LedgerReasonCode.JournalAlreadyReversed, "Journal has already been reversed.");

            if (original.ObligationId is { } obligationId)
            {
                var obligation = _obligations[obligationId];
                if (!obligation.Paid || obligation.PaidEntryId != originalJournalId)
                    throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Obligation does not reference the payment being reversed.");
            }

            var postings = original.Postings.Select(posting => new LedgerPosting(posting.AccountId, checked(-posting.AmountSen))).ToArray();
            foreach (var posting in postings)
            {
                if (_accounts[posting.AccountId].Kind is AccountKind.AgentCash or AccountKind.AgentSavings
                    && checked(_balances[posting.AccountId] + posting.AmountSen) < 0)
                    throw new LedgerValidationException(LedgerReasonCode.InsufficientFunds, "Agent account cannot be overdrawn.");
            }

            var reversal = new LedgerJournal(Guid.NewGuid(), runId, idempotencyKey, JournalKind.Reversal,
                $"reversal:{originalJournalId:N}", postings, original.ObligationId, originalJournalId, reason, initiator);
            foreach (var posting in postings)
                _balances[posting.AccountId] = checked(_balances[posting.AccountId] + posting.AmountSen);
            Append(reversal);

            if (original.ObligationId is { } paidObligationId)
                _obligations[paidObligationId] = _obligations[paidObligationId] with { Paid = false, PaidEntryId = null };

            Emit(FinanceEvents.LedgerCommitted(FinanceEvents.Operations.ReverseJournal, reversal, FinanceEvents.SourceFor(initiator)));
            Emit(FinanceEvents.JournalReversed(reversal));
            if (reversal.ObligationId is not null)
                Emit(FinanceEvents.ObligationPaymentReversed(reversal));
            return new CommitResult(reversal.JournalId, true, false);
        }
    }

    public RunInfo CreateManagedRun(JsonObject? config, string? codeRevision)
    {
        var runId = Guid.NewGuid();
        Record(FinanceEvents.Operations.CreateRun, runId, new JsonObject(), FinanceEvents.InternalApiSource, () =>
        {
            var snapshot = RunConfiguration.Validate(config, codeRevision);
            var run = new ManagedRun(snapshot, RunConfiguration.InMemoryHash(snapshot));
            _managedRuns.Add(runId, run);
            _runTransactionLimits[runId] = RunConfiguration.TransactionLimit(snapshot);
            Emit(FinanceEvents.ManagedRunCreated(snapshot, run.Hash));
            return true;
        });
        return GetRun(runId);
    }

    public RunInfo GetRun(Guid runId)
    {
        lock (_gate)
        {
            if (_managedRuns.TryGetValue(runId, out var run))
                return new RunInfo(runId, true, RunLifecycle.Wire(run.Status), run.Reason, (JsonObject)run.Snapshot.DeepClone(),
                    run.Hash, run.QueuedAt, run.StartedAt, run.FinishedAt);
            if (_runTransactionLimits.ContainsKey(runId))
                return new RunInfo(runId, false, null, null, null, null, null, null, null);
            throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
        }
    }

    public RunInfo TransitionRun(Guid runId, RunStatus to, string? reason, string source)
    {
        Record(FinanceEvents.Operations.TransitionRun, runId,
            FinanceEvents.Request(("to", RunLifecycle.Wire(to)), ("reason", reason)), source, () =>
            {
                if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
                var storedReason = RunLifecycle.NormalizeReason(to, reason);
                var from = RequireRun(runId);
                RunLifecycle.EnsureTransition(from, to);
                if (to == RunStatus.Running)
                    throw new IdempotencyConflictException(LedgerReasonCode.InvalidRunTransition, "Runs enter RUNNING only through activation.");
                SetStatus(runId, to, storedReason);
                if (AgentGatewayRules.IsTerminal(to)) RevokeSessions(runId);
                Emit(FinanceEvents.RunStatusChanged(FinanceEvents.Operations.TransitionRun, from!.Value, to, storedReason, source));
                return true;
            });
        return GetRun(runId);
    }

    public OpenRunResult ActivateRun(Guid runId, string source) =>
        Record(FinanceEvents.Operations.ActivateRun, runId, new JsonObject(), source, () =>
        {
            if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
            var from = RequireRun(runId);
            RunLifecycle.EnsureTransition(from, RunStatus.Running);
            var run = _managedRuns[runId];
            var snapshot = run.Snapshot;
            var scenario = RunConfiguration.ResolveScenario(snapshot);
            var opening = OpenRunCore(runId, RunLifecycle.OpeningIdempotencyKey, RunConfiguration.StartingBalance(snapshot),
                RunConfiguration.TransactionLimit(snapshot), FinanceEvents.Operations.ActivateRun);

            // ADR-0009: the agent's savings account; ADR-0010: same order as PostgresLedger.ActivateRun.
            CreateAccountCore(runId, AccountKind.AgentSavings, FinanceEvents.Operations.ActivateRun);
            run.VirtualStart = scenario.VirtualStartDate;
            run.VirtualDay = 0;
            foreach (var merchant in scenario.Merchants)
            {
                var accountId = Guid.NewGuid();
                _accounts.Add(accountId, new Account(runId, AccountKind.Merchant, merchant.Key, merchant.Label));
                _balances.Add(accountId, 0);
                Emit(FinanceEvents.ScenarioAccountCreated(accountId, merchant));
            }
            var schedule = ScenarioCatalog.Materialize(scenario, RunConfiguration.Seed(snapshot));
            run.Schedule.AddRange(schedule.Select(evt => new ScheduledEvent(evt)));
            Emit(FinanceEvents.ScenarioMaterialized(scenario, schedule.Count, ScenarioCatalog.ScheduleHash(schedule)));
            DeliverDueEvents(runId, run, 0, FinanceEvents.Operations.ActivateRun, source);
            SetStatus(runId, RunStatus.Running, null);
            Emit(FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ActivateRun, from!.Value, RunStatus.Running, null, source));
            return opening;
        });

    public ClockResult AdvanceClock(Guid runId, int days, string source) =>
        Record(FinanceEvents.Operations.AdvanceClock, runId, FinanceEvents.Request(("days", (long)days)), source, () =>
        {
            if (days < 1) throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Days must be positive.");
            var status = RequireRun(runId)
                ?? throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Unmanaged runs have no virtual clock.");
            RunLifecycle.EnsureActive(status, RunLifecycle.MoneyMovement);
            var run = _managedRuns[runId];
            var duration = RunConfiguration.DurationDays(run.Snapshot);
            var day = run.VirtualDay!.Value;
            if (day >= duration) return new ClockResult(day, run.VirtualStart!.Value.AddDays(day), 0, true, []);

            var newDay = Math.Min(day + days, duration);
            run.VirtualDay = newDay;
            Emit(FinanceEvents.ClockAdvanced(day, newDay, days, source));
            var delivered = DeliverDueEvents(runId, run, newDay, FinanceEvents.Operations.AdvanceClock, source);
            return new ClockResult(newDay, run.VirtualStart!.Value.AddDays(newDay), duration - newDay, newDay == duration, delivered);
        });

    public EnvironmentView GetEnvironment(Guid runId)
    {
        lock (_gate)
        {
            var status = RequireRun(runId);
            if (status is null || _managedRuns[runId].VirtualDay is null)
                throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Run has no virtual clock.");
            var run = _managedRuns[runId];
            var day = run.VirtualDay!.Value;
            var messages = run.Schedule
                .Select((scheduled, position) => (scheduled, position))
                .Where(x => x.scheduled.Event.Kind == "message" && x.scheduled.DeliveredAt is not null)
                .OrderBy(x => x.scheduled.DeliveredAt).ThenBy(x => x.position)
                .Select(x => new EnvironmentMessage(x.scheduled.Event.Key, x.scheduled.DeliveredAt!.Value,
                    x.scheduled.Event.Payload["from"]!.GetValue<string>(), x.scheduled.Event.Payload["text"]!.GetValue<string>()))
                .ToArray();
            return new EnvironmentView(run.VirtualStart!.Value.AddDays(day), day, RunConfiguration.DurationDays(run.Snapshot) - day, messages);
        }
    }

    private IReadOnlyList<string> DeliverDueEvents(Guid runId, ManagedRun run, int upToDay, string operation, string source)
    {
        var due = run.Schedule
            .Select((scheduled, position) => (scheduled, position))
            .Where(x => x.scheduled.DeliveredAt is null && x.scheduled.Event.Day <= upToDay)
            .OrderBy(x => x.scheduled.Event.Day).ThenBy(x => x.position)
            .Select(x => x.scheduled).ToArray();
        foreach (var scheduled in due)
        {
            var evt = scheduled.Event;
            Guid? obligationId = null;
            if (evt.Kind == "obligation")
            {
                var merchantKey = evt.Payload["merchant"]!.GetValue<string>();
                var merchantId = _accounts.Single(a => a.Value.RunId == runId && a.Value.ScenarioKey == merchantKey).Key;
                var (obligation, created) = CreateObligationInternal(runId, merchantId, evt.Payload["amountSen"]!.GetValue<long>(),
                    evt.Payload["invoiceReference"]!.GetValue<string>(), evt.Payload["dueDay"]!.GetValue<int>(),
                    evt.Payload["description"]!.GetValue<string>());
                if (created) Emit(FinanceEvents.ScenarioObligationCreated(obligation, operation, evt.Key, source));
                obligationId = obligation.ObligationId;
            }
            scheduled.DeliveredAt = upToDay;
            Emit(FinanceEvents.ScenarioEventDelivered(operation, evt, upToDay, obligationId, source));
        }
        return due.Select(s => s.Event.Key).ToArray();
    }

    public RunInfo? ClaimQueuedRun(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            var next = _managedRuns.Where(r => r.Value.Status == RunStatus.Queued)
                .OrderBy(r => r.Value.QueuedAt).ThenBy(r => r.Key).Select(r => (Guid?)r.Key).FirstOrDefault();
            if (next is not { } runId) return null;
            SetStatus(runId, RunStatus.Initializing, null);
            RevokeSessions(runId);
            _sessions[Convert.ToHexString(tokenHash)] = (runId, expiresAt, false);
            StoreEvent(runId, FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ClaimRun, RunStatus.Queued,
                RunStatus.Initializing, null, FinanceEvents.WorkerSource));
            return GetRun(runId);
        }
    }

    public Guid? ResolveSession(byte[] tokenHash, DateTimeOffset now, DateTimeOffset newExpiresAt)
    {
        lock (_gate)
        {
            var key = Convert.ToHexString(tokenHash);
            if (!_sessions.TryGetValue(key, out var session) || session.Revoked || session.ExpiresAt <= now) return null;
            _sessions[key] = session with { ExpiresAt = newExpiresAt };
            return session.RunId;
        }
    }

    private void RevokeSessions(Guid runId)
    {
        foreach (var key in _sessions.Where(s => s.Value.RunId == runId && !s.Value.Revoked).Select(s => s.Key).ToArray())
            _sessions[key] = _sessions[key] with { Revoked = true };
    }

    public ToolCallBegin BeginToolCall(Guid runId, string toolCallId, string tool, JsonObject redactedArguments, string requestHash)
    {
        lock (_gate)
        {
            var status = RequireRun(runId);
            if (_toolCalls.TryGetValue((runId, toolCallId), out var existing))
            {
                if (existing.RequestHash != requestHash) return new ToolCallBegin(ToolCallBeginKind.Conflict, null, null);
                if (existing.Response is not null) return new ToolCallBegin(ToolCallBeginKind.Replay, (JsonObject)existing.Response.DeepClone(), null);
                return new ToolCallBegin(ToolCallBeginKind.Resume, null, status == RunStatus.Running ? null : LedgerReasonCode.RunNotActive);
            }

            var counted = _toolCalls.Keys.Count(k => k.RunId == runId);
            _toolCalls[(runId, toolCallId)] = new ToolCallRecord(tool, (JsonObject)redactedArguments.DeepClone(), requestHash);
            StoreEvent(runId, FinanceEvents.ToolCallProposed(toolCallId, tool, redactedArguments));
            if (status != RunStatus.Running) return new ToolCallBegin(ToolCallBeginKind.New, null, LedgerReasonCode.RunNotActive);
            if (WallTimeExceeded(runId))
                return new ToolCallBegin(ToolCallBeginKind.New, null,
                    Exhaust(runId, LedgerReasonCode.WallTimeBudgetExceeded, AgentGatewayRules.WallTimeExhaustedReason, toolCallId));
            if (counted >= AgentGatewayRules.MaxActions(_managedRuns[runId].Snapshot))
                return new ToolCallBegin(ToolCallBeginKind.New, null,
                    Exhaust(runId, LedgerReasonCode.ActionBudgetExceeded, AgentGatewayRules.BudgetExhaustedReason, toolCallId));
            return new ToolCallBegin(ToolCallBeginKind.New, null, null);
        }
    }

    private bool WallTimeExceeded(Guid runId)
    {
        var run = _managedRuns[runId];
        return run.StartedAt is { } started
            && _time.GetUtcNow() >= started.AddSeconds(AgentGatewayRules.MaxWallTimeSeconds(run.Snapshot));
    }

    // Ends a RUNNING run because a budget is exhausted (ADR-0009, ADR-0011).
    private LedgerReasonCode Exhaust(Guid runId, LedgerReasonCode code, string reason, string traceId)
    {
        SetStatus(runId, RunStatus.BudgetExceeded, reason);
        RevokeSessions(runId);
        StoreEvent(runId, FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ToolCall, RunStatus.Running,
            RunStatus.BudgetExceeded, reason, FinanceEvents.GatewaySource) with { TraceId = traceId });
        return code;
    }

    // ADR-0011: records one agent turn. A retried turn is not recorded or counted twice.
    public ModelInvocationResult RecordModelInvocation(Guid runId, int turn, long inputTokens, long outputTokens, long latencyMs, string outputText)
    {
        lock (_gate)
        {
            var status = RequireRun(runId);
            var trace = FinanceEvents.TurnTrace(turn);
            var run = _managedRuns[runId];
            if (_events.TryGetValue(runId, out var events)
                && events.Any(e => e.EventType == "MODEL_INVOCATION_COMPLETED" && e.TraceId == trace))
                return new ModelInvocationResult(null, true, run.TokensUsed);
            if (status != RunStatus.Running) return new ModelInvocationResult(LedgerReasonCode.RunNotActive, false, run.TokensUsed);

            StoreEvent(runId, FinanceEvents.ModelInvocationCompleted(turn, inputTokens, outputTokens, latencyMs, outputText));
            run.TokensUsed = checked(run.TokensUsed + inputTokens + outputTokens);
            if (WallTimeExceeded(runId))
                return new ModelInvocationResult(Exhaust(runId, LedgerReasonCode.WallTimeBudgetExceeded,
                    AgentGatewayRules.WallTimeExhaustedReason, trace), false, run.TokensUsed);
            if (run.TokensUsed > AgentGatewayRules.MaxModelTokens(run.Snapshot))
                return new ModelInvocationResult(Exhaust(runId, LedgerReasonCode.TokenBudgetExceeded,
                    AgentGatewayRules.TokenExhaustedReason, trace), false, run.TokensUsed);
            return new ModelInvocationResult(null, false, run.TokensUsed);
        }
    }

    public void CompleteToolCall(Guid runId, string toolCallId, string tool, ToolCallCompletion completion)
    {
        lock (_gate)
        {
            var record = _toolCalls[(runId, toolCallId)];
            if (record.Response is not null) return;
            if (completion.ExtraEvent is { } extra) StoreEvent(runId, extra);
            record.Response = (JsonObject)completion.Response.DeepClone();
            record.Outcome = completion.Status;
            record.ReasonCode = completion.ReasonCode;
            record.JournalId = completion.JournalId;
            StoreEvent(runId, FinanceEvents.ToolReturned(toolCallId, tool, completion.Status, completion.ReasonCode, completion.JournalId));
        }
    }

    public AgentView GetAgentView(Guid runId)
    {
        lock (_gate)
        {
            RequireRun(runId);
            var accounts = _accounts.Where(a => a.Value.RunId == runId).ToArray();
            var cash = accounts.Where(a => a.Value.Kind == AccountKind.AgentCash).Select(a => (Guid?)a.Key).FirstOrDefault()
                ?? throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Run has no agent account.");
            Guid? savings = accounts.Where(a => a.Value.Kind == AccountKind.AgentSavings).Select(a => (Guid?)a.Key).FirstOrDefault();
            var merchants = accounts.Where(a => a.Value.ScenarioKey is not null)
                .Select(a => new AgentMerchant(a.Key, a.Value.ScenarioKey!, a.Value.Label!))
                .OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
            var obligations = _obligations.Where(o => o.Value.RunId == runId).Select(o => GetObligation(runId, o.Key));
            return new AgentView(cash, savings, merchants, AgentGatewayRules.Order(obligations));
        }
    }

    // Mirrors PostgresLedger.LockExistingRun: unknown → RUN_NOT_FOUND; legacy → null status.
    private RunStatus? RequireRun(Guid runId)
    {
        if (_managedRuns.TryGetValue(runId, out var run)) return run.Status;
        if (_runTransactionLimits.ContainsKey(runId)) return null;
        throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
    }

    private void SetStatus(Guid runId, RunStatus to, string? reason)
    {
        var run = _managedRuns[runId];
        var now = _time.GetUtcNow();
        run.Status = to;
        run.Reason = reason;
        if (to == RunStatus.Queued) run.QueuedAt = now;
        if (to == RunStatus.Running) run.StartedAt = now;
        if (to is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.BudgetExceeded) run.FinishedAt = now;
    }

    // ADR-0007: an operation's events are buffered and stored only if it succeeds; a rejection stores POLICY_DENIED instead.
    private T Record<T>(string operation, Guid runId, JsonObject request, string source, Func<T> body)
    {
        lock (_gate)
        {
            if (_pending is not null) return body();
            _pending = [];
            try
            {
                var result = LedgerRequestValidation.TranslateOverflow(body);
                foreach (var pending in _pending) StoreEvent(runId, pending);
                return result;
            }
            catch (LedgerException exception)
            {
                if (FinanceEvents.IsRecordable(runId, exception))
                    StoreEvent(runId, FinanceEvents.PolicyDenied(operation, exception, request, source));
                throw;
            }
            catch (Exception)
            {
                StoreEvent(runId, FinanceEvents.FinanceError(operation, LedgerReasonCode.InternalError,
                    request["idempotencyKey"]?.GetValue<string>(), source));
                throw;
            }
            finally
            {
                _pending = null;
            }
        }
    }

    private void Emit(PendingEvent pending) =>
        (_pending ?? throw new InvalidOperationException("Events can only be emitted inside a recorded operation.")).Add(pending);

    // Events are kept only for runs this ledger has opened (ADR-0007 "Unrecordable requests").
    private void StoreEvent(Guid runId, PendingEvent pending)
    {
        pending = pending.WithCallContext();
        if (!_runTransactionLimits.ContainsKey(runId)) return;
        if (!_events.TryGetValue(runId, out var events)) _events[runId] = events = [];
        events.Add(new RunEvent(runId, events.Count + 1, pending.EventType, pending.EventVersion, _time.GetUtcNow(),
            pending.Source, pending.Operation, pending.Outcome, pending.ReasonCode, pending.IdempotencyKey,
            pending.JournalId, pending.ObligationId, (JsonObject)pending.Payload.DeepClone(), null,
            _managedRuns.TryGetValue(runId, out var managed) && managed.VirtualDay is { } day
                ? new DateTimeOffset(managed.VirtualStart!.Value.AddDays(day), TimeOnly.MinValue, TimeSpan.Zero)
                : null,
            pending.TraceId));
    }

    public IReadOnlyList<RunEvent> GetEvents(Guid runId, long afterSequence, int limit)
    {
        lock (_gate)
        {
            return _events.TryGetValue(runId, out var events)
                ? events.Where(e => e.Sequence > afterSequence).Take(Math.Clamp(limit, 1, RunEventPaging.MaxLimit)).ToArray()
                : [];
        }
    }

    private void Append(LedgerJournal journal)
    {
        _journals.Add(journal);
        _journalsByKey.Add((journal.RunId, journal.IdempotencyKey), journal);
        _journalsById.Add(journal.JournalId, journal);
        if (journal.ReversesJournalId is { } originalId)
            _reversalIdsByOriginal.Add(originalId, journal.JournalId);
    }

    private CommitResult ReplayResult(LedgerJournal journal)
    {
        Guid? reversedBy = _reversalIdsByOriginal.TryGetValue(journal.JournalId, out var reversalId) ? reversalId : null;
        return new CommitResult(journal.JournalId, true, true, reversedBy is null, reversedBy);
    }

    public long GetBalance(Guid runId, Guid accountId)
    {
        lock (_gate) { if (!_accounts.TryGetValue(accountId, out var account) || account.RunId != runId) throw new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Account is not in this run."); return _balances[accountId]; }
    }
    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId) { lock (_gate) return _journals.Where(j => j.RunId == runId).ToArray(); }

    private static void ValidateRequestFields(JournalRequest request) => LedgerRequestValidation.ValidateJournalFields(request);
    private static void ValidateAmount(long amountSen) => LedgerRequestValidation.ValidateAmount(amountSen);
    private static void ValidateObligationRequest(Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference) =>
        LedgerRequestValidation.ValidateObligation(runId, merchantAccountId, amountSen, invoiceReference);

    private void ValidateAccountsAndShape(JournalRequest request)
    {
        foreach (var posting in request.Postings)
        {
            if (!_accounts.TryGetValue(posting.AccountId, out var account) || account.RunId != request.RunId)
                throw new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Every account must belong to the journal run.");
        }

        var debit = request.Postings.Single(posting => posting.AmountSen < 0);
        var credit = request.Postings.Single(posting => posting.AmountSen > 0);
        LedgerRequestValidation.ValidateAccountKinds(request.Kind, _accounts[debit.AccountId].Kind, _accounts[credit.AccountId].Kind);
    }
    private void EnsureAccountKind(Guid runId, Guid accountId, AccountKind expectedKind)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || account.RunId != runId)
            throw new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Account is not in this run.");
        if (account.Kind != expectedKind)
            throw new LedgerValidationException(LedgerReasonCode.AccountKindNotPermitted, $"Account must be a {expectedKind} account in this run.");
    }
    private static string Fingerprint(JournalRequest request) => string.Join("|", request.Kind, request.Description, string.Join(";", request.Postings.Select(p => $"{p.AccountId:N}:{p.AmountSen}")));
    private static string Fingerprint(LedgerJournal journal) => string.Join("|", journal.Kind, journal.Description, string.Join(";", journal.Postings.Select(p => $"{p.AccountId:N}:{p.AmountSen}")));
}
