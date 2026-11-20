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
public sealed record ObligationResult(Guid ObligationId, Guid MerchantAccountId, long AmountSen, bool Paid, string InvoiceReference);
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
    private sealed record Account(Guid RunId, AccountKind Kind);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, long> _balances = [];
    private readonly Dictionary<(Guid RunId, string Key), LedgerJournal> _journalsByKey = [];
    private readonly List<LedgerJournal> _journals = [];
    private readonly Dictionary<Guid, (Guid RunId, Guid MerchantAccountId, long AmountSen, bool Paid, string InvoiceReference, Guid? PaidEntryId)> _obligations = [];
    private readonly Dictionary<Guid, LedgerJournal> _journalsById = [];
    private readonly Dictionary<Guid, Guid> _reversalIdsByOriginal = [];
    private readonly Dictionary<(Guid RunId, string InvoiceReference), Guid> _obligationIdsByInvoice = [];
    private readonly Dictionary<Guid, long> _runTransactionLimits = [];

    public Guid CreateAccount(Guid runId, AccountKind kind)
    {
        if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
        if (!Enum.IsDefined(kind)) throw new LedgerValidationException(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.");
        lock (_gate) { var id = Guid.NewGuid(); _accounts.Add(id, new Account(runId, kind)); _balances.Add(id, 0); return id; }
    }

    public OpenRunResult OpenRun(
        Guid runId,
        string idempotencyKey,
        long openingBalanceSen,
        long transactionLimitSen)
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

                return new OpenRunResult(
                    existing.JournalId, true, true, cashPosting.AccountId);
            }

            if (_journals.Any(journal => journal.RunId == runId))
                throw new LedgerValidationException(
                    LedgerReasonCode.RunAlreadyFunded,
                    "Run already has ledger activity and cannot receive new opening funds.");

            var treasuryId = CreateAccount(
                runId, AccountKind.SimulatorTreasury);
            var cashId = CreateAccount(runId, AccountKind.AgentCash);

            _runTransactionLimits[runId] = transactionLimitSen;

            CommitResult result;

            try
            {
                result = Commit(new JournalRequest(
                    runId,
                    idempotencyKey,
                    JournalKind.OpeningFunding,
                    "authorized virtual opening allocation",
                    [
                        new(treasuryId, checked(-openingBalanceSen)),
                        new(cashId, openingBalanceSen)
                    ]));
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

        lock (_gate)
        {
            // Check order mirrors PostgresLedger so both return the same reason code.
            ValidateRequestFields(request);

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

                return ReplayResult(existing);
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
        long amountSen)
    {
        ValidateAmount(amountSen);

        return Commit(new JournalRequest(
            runId,
            idempotencyKey,
            JournalKind.Transfer,
            "virtual transfer",
            [
                new(sourceAccountId, checked(-amountSen)),
                new(destinationAccountId, amountSen)
            ]));
    }

    public CommitResult MoveToSavings(
        Guid runId,
        string idempotencyKey,
        Guid cashAccountId,
        Guid savingsAccountId,
        long amountSen)
    {
        ValidateAmount(amountSen);

        return Commit(new JournalRequest(
            runId,
            idempotencyKey,
            JournalKind.SavingsMove,
            "move to savings",
            [
                new(cashAccountId, checked(-amountSen)),
                new(savingsAccountId, amountSen)
            ]));
    }


    public ObligationResult CreateObligation(
        Guid runId,
        Guid merchantAccountId,
        long amountSen,
        string invoiceReference)
    {
        ValidateObligationRequest(runId, merchantAccountId, amountSen, invoiceReference);

        lock (_gate)
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

                return GetObligation(runId, existingId);
            }

            EnsureAccountKind(
                runId, merchantAccountId, AccountKind.Merchant);

            var obligationId = Guid.NewGuid();

            _obligations.Add(
                obligationId,
                (runId, merchantAccountId, amountSen,
                false, invoiceReference, null));

            _obligationIdsByInvoice.Add(invoiceKey, obligationId);

            return new ObligationResult(
                obligationId,
                merchantAccountId,
                amountSen,
                false,
                invoiceReference);
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
                obligation.InvoiceReference);
        }
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId)
    {
        lock (_gate)
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
                return ReplayResult(previous);
            }
            if (obligation.Paid) throw new LedgerValidationException(LedgerReasonCode.ObligationAlreadyPaid, "Obligation has already been paid.");
            // Account ownership and kind are checked by Commit, matching PostgresLedger.
            var result = Commit(request);
            _obligations[obligationId] = obligation with { Paid = true, PaidEntryId = result.JournalId };
            return result;
        }
    }

    // ADR-0006 full reversal. Check order mirrors PostgresLedger.ReverseJournal.
    public CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator)
    {
        LedgerRequestValidation.ValidateReversal(runId, idempotencyKey, originalJournalId, reason, initiator);

        lock (_gate)
        {
            if (_journalsByKey.TryGetValue((runId, idempotencyKey), out var existing))
            {
                LedgerRequestValidation.EnsureReversalReplay(existing, originalJournalId, reason);
                return ReplayResult(existing);
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

            return new CommitResult(reversal.JournalId, true, false);
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
