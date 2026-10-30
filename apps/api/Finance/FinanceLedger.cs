namespace Finmind.Api.Finance;

public enum AccountKind { SimulatorTreasury, AgentCash, AgentSavings, Merchant }
public enum JournalKind { OpeningFunding, BillPayment, Transfer, SavingsMove, Reversal }
public sealed record PostingRequest(Guid AccountId, long AmountSen);
public sealed record JournalRequest(Guid RunId, string IdempotencyKey, JournalKind Kind, string Description, IReadOnlyList<PostingRequest> Postings);
public sealed record LedgerPosting(Guid AccountId, long AmountSen);
public sealed record LedgerJournal(Guid JournalId, Guid RunId, string IdempotencyKey, JournalKind Kind, string Description, IReadOnlyList<LedgerPosting> Postings);
public sealed record CommitResult(Guid JournalId, bool Committed, bool WasIdempotentReplay);
public sealed record OpenRunResult(Guid JournalId, bool Committed, bool WasIdempotentReplay, Guid CashAccountId);
public sealed record ObligationResult(Guid ObligationId, Guid MerchantAccountId, long AmountSen, bool Paid);
public sealed class LedgerValidationException(string message) : Exception(message);
public sealed class IdempotencyConflictException(string message) : Exception(message);

public sealed class InMemoryLedger
{
    private sealed record Account(Guid RunId, AccountKind Kind);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, long> _balances = [];
    private readonly Dictionary<(Guid RunId, string Key), LedgerJournal> _journalsByKey = [];
    private readonly List<LedgerJournal> _journals = [];
    private readonly Dictionary<Guid, (Guid RunId, Guid MerchantAccountId, long AmountSen, bool Paid)> _obligations = [];

    public Guid CreateAccount(Guid runId, AccountKind kind)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID is required.", nameof(runId));
        lock (_gate) { var id = Guid.NewGuid(); _accounts.Add(id, new Account(runId, kind)); _balances.Add(id, 0); return id; }
    }

    public OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || openingBalanceSen <= 0) throw new LedgerValidationException("Run ID, idempotency key, and positive opening balance are required.");
        lock (_gate)
        {
            var existing = _journals.FirstOrDefault(j => j.RunId == runId && j.Kind == JournalKind.OpeningFunding);
            if (existing is not null)
            {
                var cashPosting = existing.Postings.Single(p => _accounts[p.AccountId].Kind == AccountKind.AgentCash);
                if (existing.IdempotencyKey != idempotencyKey || cashPosting.AmountSen != openingBalanceSen)
                    throw new IdempotencyConflictException("Run has already been initialized with a different opening allocation.");
                return new OpenRunResult(existing.JournalId, true, true, cashPosting.AccountId);
            }
            var treasury = CreateAccount(runId, AccountKind.SimulatorTreasury);
            var cash = CreateAccount(runId, AccountKind.AgentCash);
            var result = Commit(new JournalRequest(runId, idempotencyKey, JournalKind.OpeningFunding, "authorized virtual opening allocation", [new(treasury, checked(-openingBalanceSen)), new(cash, openingBalanceSen)]));
            return new OpenRunResult(result.JournalId, result.Committed, result.WasIdempotentReplay, cash);
        }
    }

    public CommitResult Commit(JournalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ValidateRequest(request);
            var key = (request.RunId, request.IdempotencyKey);
            var fingerprint = Fingerprint(request);
            if (_journalsByKey.TryGetValue(key, out var existing))
            {
                if (Fingerprint(existing) != fingerprint) throw new IdempotencyConflictException("Idempotency key was previously used with a different payload.");
                return new CommitResult(existing.JournalId, true, true);
            }
            if (request.Kind == JournalKind.OpeningFunding && _journals.Any(j => j.RunId == request.RunId && j.Kind == JournalKind.OpeningFunding))
                throw new LedgerValidationException("Run already has an opening funding journal.");
            var deltas = request.Postings.GroupBy(p => p.AccountId).ToDictionary(g => g.Key, g => g.Sum(p => p.AmountSen));
            foreach (var (accountId, delta) in deltas)
            {
                var next = checked(_balances[accountId] + delta);
                if (_accounts[accountId].Kind is AccountKind.AgentCash or AccountKind.AgentSavings && next < 0) throw new LedgerValidationException("Agent account cannot be overdrawn.");
            }
            var journal = new LedgerJournal(Guid.NewGuid(), request.RunId, request.IdempotencyKey, request.Kind, request.Description, request.Postings.Select(p => new LedgerPosting(p.AccountId, p.AmountSen)).ToArray());
            foreach (var (accountId, delta) in deltas) _balances[accountId] = checked(_balances[accountId] + delta);
            _journals.Add(journal); _journalsByKey.Add(key, journal);
            return new CommitResult(journal.JournalId, true, false);
        }
    }

    public bool TrySpend(Guid runId, string idempotencyKey, Guid sourceAccountId, Guid destinationAccountId, long amountSen)
    {
        if (amountSen <= 0) return false;
        try { Commit(new JournalRequest(runId, idempotencyKey, JournalKind.Transfer, "virtual transfer", [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)])); return true; }
        catch (LedgerValidationException) { return false; }
    }

    public CommitResult Transfer(Guid runId, string idempotencyKey, Guid sourceAccountId, Guid destinationAccountId, long amountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
        var request = new JournalRequest(runId, idempotencyKey, JournalKind.Transfer, "virtual transfer", [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)]);
        lock (_gate)
        {
            EnsureAccountInRun(runId, sourceAccountId);
            EnsureAccountInRun(runId, destinationAccountId);
            if (_accounts[sourceAccountId].Kind is not (AccountKind.AgentCash or AccountKind.AgentSavings)) throw new LedgerValidationException("Transfer source must be an agent account.");
            if (_accounts[destinationAccountId].Kind is not (AccountKind.Merchant or AccountKind.AgentCash or AccountKind.AgentSavings)) throw new LedgerValidationException("Transfer destination is not allowlisted.");
            return Commit(request);
        }
    }

    public CommitResult MoveToSavings(Guid runId, string idempotencyKey, Guid cashAccountId, Guid savingsAccountId, long amountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
        var request = new JournalRequest(runId, idempotencyKey, JournalKind.SavingsMove, "move to savings", [new(cashAccountId, checked(-amountSen)), new(savingsAccountId, amountSen)]);
        lock (_gate) { EnsureAccountKind(runId, cashAccountId, AccountKind.AgentCash); EnsureAccountKind(runId, savingsAccountId, AccountKind.AgentSavings); return Commit(request); }
    }

    public CommitResult PayBill(Guid runId, string idempotencyKey, Guid cashAccountId, Guid merchantAccountId, long amountSen, long approvedObligationAmountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
        if (amountSen != approvedObligationAmountSen) throw new LedgerValidationException("Payment must match the server-approved obligation amount.");
        var request = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, "approved virtual bill payment", [new(cashAccountId, checked(-amountSen)), new(merchantAccountId, amountSen)]);
        lock (_gate) { EnsureAccountKind(runId, cashAccountId, AccountKind.AgentCash); EnsureAccountKind(runId, merchantAccountId, AccountKind.Merchant); return Commit(request); }
    }

    public ObligationResult CreateObligation(Guid runId, Guid merchantAccountId, long amountSen)
    {
        if (runId == Guid.Empty || amountSen <= 0) throw new LedgerValidationException("Run ID and positive obligation amount are required.");
        lock (_gate)
        {
            EnsureAccountKind(runId, merchantAccountId, AccountKind.Merchant);
            var obligationId = Guid.NewGuid();
            _obligations.Add(obligationId, (runId, merchantAccountId, amountSen, false));
            return new ObligationResult(obligationId, merchantAccountId, amountSen, false);
        }
    }

    public ObligationResult GetObligation(Guid runId, Guid obligationId)
    {
        lock (_gate)
        {
            if (!_obligations.TryGetValue(obligationId, out var obligation) || obligation.RunId != runId)
                throw new LedgerValidationException("Obligation not found in this run.");
            return new ObligationResult(obligationId, obligation.MerchantAccountId, obligation.AmountSen, obligation.Paid);
        }
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId, long transactionLimitSen)
    {
        lock (_gate)
        {
            if (!_obligations.TryGetValue(obligationId, out var obligation) || obligation.RunId != runId)
                throw new LedgerValidationException("Obligation not found in this run.");

            // Permit retries of a payment already committed with this key, but never
            // allow a second key to pay the same obligation again.
            var paymentKey = (runId, idempotencyKey);
            if (_journalsByKey.TryGetValue(paymentKey, out var previous))
            {
                var expected = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, $"obligation:{obligationId:N}",
                    [new(cashAccountId, checked(-obligation.AmountSen)), new(obligation.MerchantAccountId, obligation.AmountSen)]);
                if (Fingerprint(previous) != Fingerprint(expected)) throw new IdempotencyConflictException("Idempotency key was previously used with a different payload.");
                return new CommitResult(previous.JournalId, true, true);
            }
            if (obligation.Paid) throw new LedgerValidationException("Obligation has already been paid.");
            ValidateAmount(obligation.AmountSen, transactionLimitSen);
            EnsureAccountKind(runId, cashAccountId, AccountKind.AgentCash);
            var request = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, $"obligation:{obligationId:N}",
                [new(cashAccountId, checked(-obligation.AmountSen)), new(obligation.MerchantAccountId, obligation.AmountSen)]);
            var result = Commit(request);
            _obligations[obligationId] = obligation with { Paid = true };
            return result;
        }
    }

    public long GetBalance(Guid runId, Guid accountId)
    {
        lock (_gate) { if (!_accounts.TryGetValue(accountId, out var account) || account.RunId != runId) throw new LedgerValidationException("Account is not in this run."); return _balances[accountId]; }
    }
    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId) { lock (_gate) return _journals.Where(j => j.RunId == runId).ToArray(); }

    private void ValidateRequest(JournalRequest request)
    {
        if (request.RunId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.Description)) throw new LedgerValidationException("Run, idempotency key, and description are required.");
        if (request.Postings is null || request.Postings.Count < 2) throw new LedgerValidationException("A journal requires at least two postings.");
        long total = 0;
        foreach (var posting in request.Postings)
        {
            if (posting.AmountSen == 0) throw new LedgerValidationException("Zero-value postings are not allowed.");
            if (!_accounts.TryGetValue(posting.AccountId, out var account) || account.RunId != request.RunId) throw new LedgerValidationException("Every account must belong to the journal run.");
            total = checked(total + posting.AmountSen);
        }
        if (total != 0) throw new LedgerValidationException("Journal postings must balance to zero.");
        ValidateJournalShape(request);
    }

    private void ValidateJournalShape(JournalRequest request)
    {
        if (request.Postings.Count != 2 || request.Postings[0].AccountId == request.Postings[1].AccountId)
            throw new LedgerValidationException("This journal kind requires exactly two distinct accounts.");

        var debit = request.Postings.Single(posting => posting.AmountSen < 0);
        var credit = request.Postings.Single(posting => posting.AmountSen > 0);
        var debitKind = _accounts[debit.AccountId].Kind;
        var creditKind = _accounts[credit.AccountId].Kind;
        var valid = request.Kind switch
        {
            JournalKind.OpeningFunding => debitKind == AccountKind.SimulatorTreasury && creditKind == AccountKind.AgentCash,
            JournalKind.Transfer => debitKind is AccountKind.AgentCash or AccountKind.AgentSavings && creditKind is AccountKind.Merchant or AccountKind.AgentCash or AccountKind.AgentSavings,
            JournalKind.SavingsMove => debitKind == AccountKind.AgentCash && creditKind == AccountKind.AgentSavings,
            JournalKind.BillPayment => debitKind == AccountKind.AgentCash && creditKind == AccountKind.Merchant,
            JournalKind.Reversal => false,
            _ => false
        };

        if (!valid) throw new LedgerValidationException("Posting account kinds are not permitted for this journal kind.");
    }
    private static void ValidateAmount(long amountSen, long transactionLimitSen)
    {
        if (amountSen <= 0) throw new LedgerValidationException("Amount must be positive.");
        if (transactionLimitSen <= 0 || amountSen > transactionLimitSen) throw new LedgerValidationException("Transaction limit exceeded.");
    }
    private void EnsureAccountKind(Guid runId, Guid accountId, AccountKind expectedKind)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || account.RunId != runId || account.Kind != expectedKind)
            throw new LedgerValidationException($"Account must be a {expectedKind} account in this run.");
    }
    private void EnsureAccountInRun(Guid runId, Guid accountId)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || account.RunId != runId) throw new LedgerValidationException("Account is not in this run.");
    }
    private static string Fingerprint(JournalRequest request) => string.Join("|", request.Kind, request.Description, string.Join(";", request.Postings.Select(p => $"{p.AccountId:N}:{p.AmountSen}")));
    private static string Fingerprint(LedgerJournal journal) => string.Join("|", journal.Kind, journal.Description, string.Join(";", journal.Postings.Select(p => $"{p.AccountId:N}:{p.AmountSen}")));
}
