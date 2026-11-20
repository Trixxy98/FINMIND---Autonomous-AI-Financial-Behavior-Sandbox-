using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Finmind.Api.Finance;

public sealed class PostgresLedger(NpgsqlDataSource dataSource) : IFinanceLedger
{
    public Guid CreateAccount(Guid runId, AccountKind kind)
    {
        if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
        if (!Enum.IsDefined(kind)) throw new LedgerValidationException(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        LockExistingRun(connection, transaction, runId);
        if (GetRunTransactionLimit(connection, transaction, runId) is null)
            throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Run transaction policy is not configured.");

        var accountId = Guid.NewGuid();
        InsertAccount(connection, transaction, runId, accountId, kind);
        transaction.Commit();
        return accountId;
    }

    public OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID and idempotency key are required.");
        if (openingBalanceSen <= 0 || transactionLimitSen <= 0)
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Opening balance and transaction limit must be positive sen.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        EnsureRun(connection, transaction, runId, transactionLimitSen);
        LockRun(connection, transaction, runId);

        var configuredLimit = GetRunTransactionLimit(connection, transaction, runId);
        if (configuredLimit is null)
        {
            if (HasAnyJournal(connection, transaction, runId))
                throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Existing run has ledger activity but no transaction policy; create a new run.");
            SetRunTransactionLimit(connection, transaction, runId, transactionLimitSen);
            configuredLimit = transactionLimitSen;
        }
        else if (configuredLimit.Value != transactionLimitSen)
        {
            throw new IdempotencyConflictException(LedgerReasonCode.RunPolicyConflict, "Run already has a different transaction policy.");
        }

        var existingKey = FindJournalByKey(connection, transaction, runId, idempotencyKey);
        if (existingKey is not null && existingKey.Value != JournalKind.OpeningFunding)
            throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used for a different operation.");

        var existingOpening = FindOpening(connection, transaction, runId);
        if (existingOpening is not null)
        {
            if (existingOpening.Value.IdempotencyKey != idempotencyKey || existingOpening.Value.AmountSen != openingBalanceSen)
                throw new IdempotencyConflictException(LedgerReasonCode.RunPolicyConflict, "Run has already been initialized with a different opening allocation.");

            transaction.Commit();
            return new OpenRunResult(existingOpening.Value.JournalId, true, true, existingOpening.Value.CashAccountId);
        }

        if (HasAnyJournal(connection, transaction, runId))
            throw new LedgerValidationException(LedgerReasonCode.RunAlreadyFunded, "Run already has ledger activity and cannot receive new opening funds.");

        var journalId = Guid.NewGuid();
        var treasuryId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var negativeAmount = checked(-openingBalanceSen);
        InsertAccount(connection, transaction, runId, treasuryId, AccountKind.SimulatorTreasury);
        InsertAccount(connection, transaction, runId, cashId, AccountKind.AgentCash);
        InsertJournal(connection, transaction, journalId, runId, JournalKind.OpeningFunding,
            idempotencyKey, "authorized virtual opening allocation");
        InsertPosting(connection, transaction, journalId, runId, treasuryId, negativeAmount);
        InsertPosting(connection, transaction, journalId, runId, cashId, openingBalanceSen);
        transaction.Commit();

        return new OpenRunResult(journalId, true, false, cashId);
    }

    public long GetBalance(Guid runId, Guid accountId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT COALESCE(SUM(p.signed_amount_sen), 0)::BIGINT " +
            "FROM accounts a LEFT JOIN journal_postings p " +
            "ON p.account_id = a.id AND p.run_id = a.run_id AND p.currency = a.currency " +
            "WHERE a.id = @account_id AND a.run_id = @run_id " +
            "GROUP BY a.id", connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("run_id", runId);
        var result = command.ExecuteScalar();
        if (result is null) throw new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Account is not in this run.");
        return (long)result;
    }

    public CommitResult Transfer(Guid runId, string idempotencyKey, Guid sourceAccountId,
        Guid destinationAccountId, long amountSen)
    {
        LedgerRequestValidation.ValidateAmount(amountSen);
        return Commit(new JournalRequest(runId, idempotencyKey, JournalKind.Transfer,
            "virtual transfer", [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)]));
    }

    public bool TrySpend(Guid runId, string idempotencyKey, Guid sourceAccountId,
        Guid destinationAccountId, long amountSen)
    {
        if (amountSen <= 0) return false;
        try
        {
            Commit(new JournalRequest(runId, idempotencyKey, JournalKind.Transfer, "virtual transfer",
                [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)]));
            return true;
        }
        catch (LedgerValidationException)
        {
            return false;
        }
    }

    public CommitResult MoveToSavings(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid savingsAccountId, long amountSen)
    {
        LedgerRequestValidation.ValidateAmount(amountSen);
        return Commit(new JournalRequest(runId, idempotencyKey, JournalKind.SavingsMove,
            "move to savings", [new(cashAccountId, checked(-amountSen)), new(savingsAccountId, amountSen)]));
    }

    public ObligationResult CreateObligation(
        Guid runId,
        Guid merchantAccountId,
        long amountSen,
        string invoiceReference)
    {
        LedgerRequestValidation.ValidateObligation(
            runId, merchantAccountId, amountSen, invoiceReference);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(
            IsolationLevel.ReadCommitted);

        LockExistingRun(connection, transaction, runId);

        if (GetRunTransactionLimit(connection, transaction, runId) is null)
            throw new LedgerValidationException(
                LedgerReasonCode.RunPolicyNotConfigured,
                "Run transaction policy is not configured.");

        using (var existingCommand = new NpgsqlCommand(
            """
            SELECT id, merchant_account_id, amount_sen, paid,
                invoice_reference
            FROM obligations
            WHERE run_id = @run_id
            AND invoice_reference = @invoice_reference
            """,
            connection, transaction))
        {
            existingCommand.Parameters.AddWithValue("run_id", runId);
            existingCommand.Parameters.AddWithValue(
                "invoice_reference", invoiceReference);

            using var reader = existingCommand.ExecuteReader();

            if (reader.Read())
            {
                var existing = new ObligationResult(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt64(2),
                    reader.GetBoolean(3),
                    reader.GetString(4));

                reader.Close();

                if (existing.MerchantAccountId != merchantAccountId
                    || existing.AmountSen != amountSen)
                {
                    throw new IdempotencyConflictException(
                        LedgerReasonCode.InvoiceReferenceConflict,
                        "Invoice reference was previously used with a different payload.");
                }

                transaction.Commit();
                return existing;
            }
        }

        var kinds = LoadAccountKinds(
            connection,
            transaction,
            runId,
            [new PostingRequest(merchantAccountId, 1)]);

        if (kinds.Count != 1)
            throw new LedgerValidationException(
                LedgerReasonCode.AccountNotInRun,
                "Account is not in this run.");

        if (kinds[merchantAccountId] != AccountKind.Merchant)
            throw new LedgerValidationException(
                LedgerReasonCode.AccountKindNotPermitted,
                "Obligation payee must be a merchant in this run.");

        var obligationId = Guid.NewGuid();

        using var command = new NpgsqlCommand(
            """
            INSERT INTO obligations
                (id, run_id, merchant_account_id,
                amount_sen, invoice_reference)
            VALUES
                (@id, @run_id, @merchant_id,
                @amount_sen, @invoice_reference)
            """,
            connection, transaction);

        command.Parameters.AddWithValue("id", obligationId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("merchant_id", merchantAccountId);
        command.Parameters.AddWithValue("amount_sen", amountSen);
        command.Parameters.AddWithValue(
            "invoice_reference", invoiceReference);

        command.ExecuteNonQuery();
        transaction.Commit();

        return new ObligationResult(
            obligationId,
            merchantAccountId,
            amountSen,
            false,
            invoiceReference);
    }

    public ObligationResult GetObligation(
        Guid runId,
        Guid obligationId)
    {
        using var connection = dataSource.OpenConnection();

        using var command = new NpgsqlCommand(
            """
            SELECT merchant_account_id, amount_sen, paid,
                invoice_reference
            FROM obligations
            WHERE id = @id AND run_id = @run_id
            """,
            connection);

        command.Parameters.AddWithValue("id", obligationId);
        command.Parameters.AddWithValue("run_id", runId);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
            throw new LedgerValidationException(
                LedgerReasonCode.ObligationNotFound,
                "Obligation not found in this run.");

        return new ObligationResult(
            obligationId,
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetString(3));
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid obligationId)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockExistingRun(connection, transaction, runId);

        using var obligationCommand = new NpgsqlCommand(
            "SELECT merchant_account_id, amount_sen, paid FROM obligations " +
            "WHERE id = @id AND run_id = @run_id FOR UPDATE", connection, transaction);
        obligationCommand.Parameters.AddWithValue("id", obligationId);
        obligationCommand.Parameters.AddWithValue("run_id", runId);
        using var reader = obligationCommand.ExecuteReader();
        if (!reader.Read()) throw new LedgerValidationException(LedgerReasonCode.ObligationNotFound, "Obligation not found in this run.");
        var merchantAccountId = reader.GetGuid(0);
        var amountSen = reader.GetInt64(1);
        var isPaid = reader.GetBoolean(2);
        reader.Close();

        var description = $"obligation:{obligationId:N}";
        var request = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, description,
            [new(cashAccountId, checked(-amountSen)), new(merchantAccountId, amountSen)], obligationId);
        ValidateRequestFields(request);
        var previous = LoadJournalByKey(connection, transaction, runId, idempotencyKey);
        if (previous is not null)
        {
            if (Fingerprint(previous) != Fingerprint(request))
                throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used with a different payload.");
            var replay = ReplayResult(connection, transaction, previous);
            transaction.Commit();
            return replay;
        }
        if (isPaid) throw new LedgerValidationException(LedgerReasonCode.ObligationAlreadyPaid, "Obligation has already been paid.");

        var result = CommitWithinTransaction(connection, transaction, request);
        using var update = new NpgsqlCommand(
            "UPDATE obligations SET paid = TRUE, paid_entry_id = @entry_id " +
            "WHERE id = @id AND run_id = @run_id AND paid = FALSE", connection, transaction);
        update.Parameters.AddWithValue("entry_id", result.JournalId);
        update.Parameters.AddWithValue("id", obligationId);
        update.Parameters.AddWithValue("run_id", runId);
        if (update.ExecuteNonQuery() != 1) throw new LedgerValidationException(LedgerReasonCode.ConcurrentStateChange, "Obligation payment state changed concurrently.");
        transaction.Commit();
        return result;
    }

    // ADR-0006: append-only full reversal; obligation returns to UNPAID in the same transaction.
    // Insert order matters: the obligation guard trigger requires the reversal journal to exist first.
    public CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator)
    {
        LedgerRequestValidation.ValidateReversal(runId, idempotencyKey, originalJournalId, reason, initiator);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockExistingRun(connection, transaction, runId);

        var existing = LoadJournalByKey(connection, transaction, runId, idempotencyKey);
        if (existing is not null)
        {
            LedgerRequestValidation.EnsureReversalReplay(existing, originalJournalId, reason);
            var replay = ReplayResult(connection, transaction, existing);
            transaction.Commit();
            return replay;
        }

        var original = LoadJournalById(connection, transaction, runId, originalJournalId)
            ?? throw new LedgerValidationException(LedgerReasonCode.JournalNotFound, "Journal not found in this run.");
        LedgerRequestValidation.EnsureReversible(original);
        if (FindReversalOf(connection, transaction, runId, originalJournalId) is not null)
            throw new LedgerValidationException(LedgerReasonCode.JournalAlreadyReversed, "Journal has already been reversed.");

        if (original.ObligationId is { } obligationId)
        {
            using var lockObligation = new NpgsqlCommand(
                "SELECT paid, paid_entry_id FROM obligations WHERE id = @id AND run_id = @run_id FOR UPDATE",
                connection, transaction);
            lockObligation.Parameters.AddWithValue("id", obligationId);
            lockObligation.Parameters.AddWithValue("run_id", runId);
            using var reader = lockObligation.ExecuteReader();
            if (!reader.Read() || !reader.GetBoolean(0) || reader.IsDBNull(1) || reader.GetGuid(1) != originalJournalId)
                throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Obligation does not reference the payment being reversed.");
        }

        var postings = original.Postings
            .Select(posting => new PostingRequest(posting.AccountId, checked(-posting.AmountSen))).ToArray();
        var kinds = LoadAccountKinds(connection, transaction, runId, postings);
        foreach (var posting in postings)
        {
            if (kinds[posting.AccountId] is AccountKind.AgentCash or AccountKind.AgentSavings
                && checked(GetBalance(connection, transaction, runId, posting.AccountId) + posting.AmountSen) < 0)
                throw new LedgerValidationException(LedgerReasonCode.InsufficientFunds, "Agent account cannot be overdrawn.");
        }

        var reversalId = Guid.NewGuid();
        InsertJournal(connection, transaction, reversalId, runId, JournalKind.Reversal, idempotencyKey,
            $"reversal:{originalJournalId:N}", original.ObligationId, originalJournalId, reason, initiator);
        foreach (var posting in postings)
            InsertPosting(connection, transaction, reversalId, runId, posting.AccountId, posting.AmountSen);

        if (original.ObligationId is { } paidObligationId)
        {
            using var update = new NpgsqlCommand(
                "UPDATE obligations SET paid = FALSE, paid_entry_id = NULL " +
                "WHERE id = @id AND run_id = @run_id AND paid = TRUE AND paid_entry_id = @original_id",
                connection, transaction);
            update.Parameters.AddWithValue("id", paidObligationId);
            update.Parameters.AddWithValue("run_id", runId);
            update.Parameters.AddWithValue("original_id", originalJournalId);
            if (update.ExecuteNonQuery() != 1)
                throw new LedgerValidationException(LedgerReasonCode.ConcurrentStateChange, "Obligation payment state changed concurrently.");
        }

        transaction.Commit();
        return new CommitResult(reversalId, true, false);
    }

    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        return LoadJournals(connection, null, runId, string.Empty, _ => { });
    }

    public CommitResult Commit(JournalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestFields(request);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockExistingRun(connection, transaction, request.RunId);
        if (GetRunTransactionLimit(connection, transaction, request.RunId) is null)
            throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Run transaction policy is not configured.");
        var result = CommitWithinTransaction(connection, transaction, request);
        transaction.Commit();
        return result;
    }

    private static CommitResult CommitWithinTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
        JournalRequest request)
    {
        var existing = LoadJournalByKey(connection, transaction, request.RunId, request.IdempotencyKey);
        if (existing is not null)
        {
            if (Fingerprint(existing) != Fingerprint(request))
                throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used with a different payload.");
            return ReplayResult(connection, transaction, existing);
        }

        var accountKinds = LoadAccountKinds(connection, transaction, request.RunId, request.Postings);
        ValidateJournalShape(request, accountKinds);

        if (request.Kind != JournalKind.OpeningFunding)
        {
            var debit = request.Postings.Single(posting => posting.AmountSen < 0);
            var transactionLimitSen = GetRunTransactionLimit(connection, transaction, request.RunId)
                ?? throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Run transaction policy is not configured.");
            if (checked(-debit.AmountSen) > transactionLimitSen)
                throw new LedgerValidationException(LedgerReasonCode.ExceedsTransactionLimit, "Run transaction limit exceeded.");
        }

        if (request.Kind == JournalKind.OpeningFunding && HasOpeningFunding(connection, transaction, request.RunId))
            throw new LedgerValidationException(LedgerReasonCode.RunAlreadyFunded, "Run already has an opening funding journal.");

        var deltas = request.Postings.GroupBy(posting => posting.AccountId)
            .ToDictionary(group => group.Key, group => group.Aggregate(0L, (sum, posting) => checked(sum + posting.AmountSen)));
        foreach (var (accountId, delta) in deltas)
        {
            var kind = accountKinds[accountId];
            if (kind is AccountKind.AgentCash or AccountKind.AgentSavings)
            {
                var balance = GetBalance(connection, transaction, request.RunId, accountId);
                if (checked(balance + delta) < 0)
                    throw new LedgerValidationException(LedgerReasonCode.InsufficientFunds, "Agent account cannot be overdrawn.");
            }
        }

        var journalId = Guid.NewGuid();
        InsertJournal(connection, transaction, journalId, request.RunId, request.Kind,
            request.IdempotencyKey, request.Description, request.ObligationId);
        foreach (var posting in request.Postings)
            InsertPosting(connection, transaction, journalId, request.RunId, posting.AccountId, posting.AmountSen);
        return new CommitResult(journalId, true, false);
    }

    private static void ValidateRequestFields(JournalRequest request) =>
        LedgerRequestValidation.ValidateJournalFields(request);

    private static void ValidateJournalShape(JournalRequest request, IReadOnlyDictionary<Guid, AccountKind> kinds)
    {
        if (kinds.Count != request.Postings.Count)
            throw new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Every account must belong to the journal run.");
        var debit = request.Postings.Single(posting => posting.AmountSen < 0);
        var credit = request.Postings.Single(posting => posting.AmountSen > 0);
        LedgerRequestValidation.ValidateAccountKinds(request.Kind, kinds[debit.AccountId], kinds[credit.AccountId]);
    }

    private static void LockExistingRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT id FROM experiment_runs WHERE id = @run_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        if (command.ExecuteScalar() is null)
            throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
    }

    private static IReadOnlyDictionary<Guid, AccountKind> LoadAccountKinds(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid runId, IReadOnlyList<PostingRequest> postings)
    {
        using var command = new NpgsqlCommand(
            "SELECT id, kind FROM accounts WHERE run_id = @run_id AND id = ANY(@account_ids) ORDER BY id",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_ids", postings.Select(posting => posting.AccountId).Distinct().ToArray());
        using var reader = command.ExecuteReader();
        var result = new Dictionary<Guid, AccountKind>();
        while (reader.Read()) result.Add(reader.GetGuid(0), FromDatabaseValue<AccountKind>(reader.GetString(1)));
        return result;
    }

    private static bool HasOpeningFunding(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM journal_entries WHERE run_id = @run_id AND kind = 'opening_funding')",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        return (bool)command.ExecuteScalar()!;
    }

    private static bool HasAnyJournal(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM journal_entries WHERE run_id = @run_id)", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        return (bool)command.ExecuteScalar()!;
    }

    private static long GetBalance(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, Guid accountId)
    {
        using var command = new NpgsqlCommand(
            "SELECT COALESCE(SUM(signed_amount_sen), 0)::BIGINT FROM journal_postings " +
            "WHERE run_id = @run_id AND account_id = @account_id", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", accountId);
        return (long)command.ExecuteScalar()!;
    }

    private static LedgerJournal? LoadJournalByKey(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, string idempotencyKey) =>
        LoadJournals(connection, transaction, runId, " AND e.idempotency_key = @key",
            command => command.Parameters.AddWithValue("key", idempotencyKey)).SingleOrDefault();

    private static LedgerJournal? LoadJournalById(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, Guid journalId) =>
        LoadJournals(connection, transaction, runId, " AND e.id = @journal_id",
            command => command.Parameters.AddWithValue("journal_id", journalId)).SingleOrDefault();

    private static List<LedgerJournal> LoadJournals(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid runId, string filter, Action<NpgsqlCommand> bind)
    {
        using var command = new NpgsqlCommand(
            "SELECT e.id, e.kind, e.idempotency_key, e.description, e.obligation_id, e.reverses_entry_id, " +
            "e.reversal_reason, e.initiator_type, p.account_id, p.signed_amount_sen " +
            "FROM journal_entries e JOIN journal_postings p ON p.entry_id = e.id AND p.run_id = e.run_id " +
            "WHERE e.run_id = @run_id" + filter + " ORDER BY e.created_at, e.id, p.id", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        bind(command);
        using var reader = command.ExecuteReader();
        var journals = new List<LedgerJournal>();
        LedgerJournal? current = null;
        var postings = new List<LedgerPosting>();

        while (reader.Read())
        {
            var journalId = reader.GetGuid(0);
            if (current?.JournalId != journalId)
            {
                if (current is not null) journals.Add(current with { Postings = postings });
                postings = [];
                current = new LedgerJournal(
                    journalId, runId, reader.GetString(2), FromDatabaseValue<JournalKind>(reader.GetString(1)),
                    reader.GetString(3), [],
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : FromDatabaseValue<ReversalInitiator>(reader.GetString(7)));
            }

            postings.Add(new LedgerPosting(reader.GetGuid(8), reader.GetInt64(9)));
        }

        if (current is not null) journals.Add(current with { Postings = postings });
        return journals;
    }

    private static Guid? FindReversalOf(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid journalId)
    {
        using var command = new NpgsqlCommand(
            "SELECT id FROM journal_entries WHERE run_id = @run_id AND reverses_entry_id = @journal_id",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("journal_id", journalId);
        return command.ExecuteScalar() as Guid?;
    }

    private static CommitResult ReplayResult(NpgsqlConnection connection, NpgsqlTransaction transaction, LedgerJournal journal)
    {
        var reversedBy = FindReversalOf(connection, transaction, journal.RunId, journal.JournalId);
        return new CommitResult(journal.JournalId, true, true, reversedBy is null, reversedBy);
    }

    private static string Fingerprint(JournalRequest request) => string.Join("|", request.Kind,
        request.Description, string.Join(";", request.Postings.Select(posting => $"{posting.AccountId:N}:{posting.AmountSen}")));

    private static string Fingerprint(LedgerJournal journal) => string.Join("|", journal.Kind,
        journal.Description, string.Join(";", journal.Postings.Select(posting => $"{posting.AccountId:N}:{posting.AmountSen}")));


    private static void EnsureRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, long transactionLimitSen)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO experiment_runs (id, transaction_limit_sen) VALUES (@run_id, @transaction_limit_sen) " +
            "ON CONFLICT (id) DO NOTHING",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("transaction_limit_sen", transactionLimitSen);
        command.ExecuteNonQuery();
    }

    private static void LockRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT id FROM experiment_runs WHERE id = @run_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.ExecuteScalar();
    }

    private static JournalKind? FindJournalByKey(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string idempotencyKey)
    {
        using var command = new NpgsqlCommand(
            "SELECT kind FROM journal_entries WHERE run_id = @run_id AND idempotency_key = @key",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        var result = command.ExecuteScalar();
        return result is null ? null : FromDatabaseValue<JournalKind>((string)result);
    }

    private static long? GetRunTransactionLimit(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT transaction_limit_sen FROM experiment_runs WHERE id = @run_id", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (long)result;
    }

    private static void SetRunTransactionLimit(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, long transactionLimitSen)
    {
        using var command = new NpgsqlCommand(
            "UPDATE experiment_runs SET transaction_limit_sen = @transaction_limit_sen " +
            "WHERE id = @run_id AND transaction_limit_sen IS NULL", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("transaction_limit_sen", transactionLimitSen);
        if (command.ExecuteNonQuery() != 1)
            throw new LedgerValidationException(LedgerReasonCode.ConcurrentStateChange, "Could not configure the run transaction policy.");
    }

    private static (Guid JournalId, string IdempotencyKey, Guid CashAccountId, long AmountSen)? FindOpening(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT e.id, e.idempotency_key, p.account_id, p.signed_amount_sen " +
            "FROM journal_entries e " +
            "JOIN journal_postings p ON p.entry_id = e.id AND p.run_id = e.run_id " +
            "JOIN accounts a ON a.id = p.account_id AND a.run_id = p.run_id " +
            "WHERE e.run_id = @run_id AND e.kind = 'opening_funding' AND a.kind = 'agent_cash'",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3));
    }

    private static void InsertAccount(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, Guid accountId, AccountKind kind)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO accounts (id, run_id, kind) VALUES (@id, @run_id, @kind)", connection, transaction);
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("kind", ToDatabaseValue(kind));
        command.ExecuteNonQuery();
    }

    private static void InsertJournal(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid journalId, Guid runId, JournalKind kind, string idempotencyKey, string description,
        Guid? obligationId = null, Guid? reversesJournalId = null, string? reversalReason = null, ReversalInitiator? initiator = null)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description, " +
            "obligation_id, reverses_entry_id, reversal_reason, initiator_type) " +
            "VALUES (@id, @run_id, @kind, @key, @description, @obligation_id, @reverses_entry_id, @reversal_reason, @initiator_type)",
            connection, transaction);
        command.Parameters.AddWithValue("id", journalId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("kind", ToDatabaseValue(kind));
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("obligation_id", NpgsqlDbType.Uuid, (object?)obligationId ?? DBNull.Value);
        command.Parameters.AddWithValue("reverses_entry_id", NpgsqlDbType.Uuid, (object?)reversesJournalId ?? DBNull.Value);
        command.Parameters.AddWithValue("reversal_reason", NpgsqlDbType.Text, (object?)reversalReason ?? DBNull.Value);
        command.Parameters.AddWithValue("initiator_type", NpgsqlDbType.Text,
            initiator is { } value ? ToDatabaseValue(value) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertPosting(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid journalId, Guid runId, Guid accountId, long amountSen)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO journal_postings (entry_id, run_id, account_id, currency, signed_amount_sen) " +
            "VALUES (@entry_id, @run_id, @account_id, 'MYR', @amount_sen)", connection, transaction);
        command.Parameters.AddWithValue("entry_id", journalId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("amount_sen", NpgsqlDbType.Bigint, amountSen);
        command.ExecuteNonQuery();
    }

    private static string ToDatabaseValue(AccountKind kind) => kind switch
    {
        AccountKind.SimulatorTreasury => "simulator_treasury",
        AccountKind.AgentCash => "agent_cash",
        AccountKind.AgentSavings => "agent_savings",
        AccountKind.Merchant => "merchant",
        _ => throw new LedgerValidationException(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.")
    };

    private static string ToDatabaseValue(JournalKind kind) => kind switch
    {
        JournalKind.OpeningFunding => "opening_funding",
        JournalKind.Transfer => "transfer",
        JournalKind.SavingsMove => "savings_move",
        JournalKind.BillPayment => "bill_payment",
        JournalKind.Reversal => "reversal",
        _ => throw new LedgerValidationException(LedgerReasonCode.LedgerInvariantViolation, "Unsupported journal kind.")
    };

    private static string ToDatabaseValue(ReversalInitiator initiator) => initiator switch
    {
        ReversalInitiator.Researcher => "researcher",
        ReversalInitiator.ScenarioEngine => "scenario_engine",
        _ => throw new LedgerValidationException(LedgerReasonCode.UnsupportedInitiatorType, "Unsupported reversal initiator.")
    };

    private static T FromDatabaseValue<T>(string value) where T : struct, Enum
    {
        var normalized = string.Concat(value.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (Enum.TryParse<T>(normalized, out var parsed)) return parsed;
        throw new LedgerValidationException(LedgerReasonCode.InternalError, "Unsupported value stored in ledger.");
    }
}
