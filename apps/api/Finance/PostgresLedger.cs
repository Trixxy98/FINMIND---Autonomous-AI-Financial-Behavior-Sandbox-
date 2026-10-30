using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Finmind.Api.Finance;

public sealed class PostgresLedger(NpgsqlDataSource dataSource)
{
    public Guid CreateAccount(Guid runId, AccountKind kind)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID is required.", nameof(runId));
        if (!Enum.IsDefined(kind)) throw new LedgerValidationException("Unsupported account kind.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureRun(connection, transaction, runId);
        LockRun(connection, transaction, runId);

        var accountId = Guid.NewGuid();
        InsertAccount(connection, transaction, runId, accountId, kind);
        transaction.Commit();
        return accountId;
    }

    public OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || openingBalanceSen <= 0)
            throw new LedgerValidationException("Run ID, idempotency key, and positive opening balance are required.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        EnsureRun(connection, transaction, runId);
        LockRun(connection, transaction, runId);

        var existingKey = FindJournalByKey(connection, transaction, runId, idempotencyKey);
        if (existingKey is not null && existingKey.Value != JournalKind.OpeningFunding)
            throw new IdempotencyConflictException("Idempotency key was previously used for a different operation.");

        var existingOpening = FindOpening(connection, transaction, runId);
        if (existingOpening is not null)
        {
            if (existingOpening.Value.IdempotencyKey != idempotencyKey || existingOpening.Value.AmountSen != openingBalanceSen)
                throw new IdempotencyConflictException("Run has already been initialized with a different opening allocation.");

            transaction.Commit();
            return new OpenRunResult(existingOpening.Value.JournalId, true, true, existingOpening.Value.CashAccountId);
        }

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
        if (result is null) throw new LedgerValidationException("Account is not in this run.");
        return (long)result;
    }

    public CommitResult Transfer(Guid runId, string idempotencyKey, Guid sourceAccountId,
        Guid destinationAccountId, long amountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
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
        Guid savingsAccountId, long amountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
        return Commit(new JournalRequest(runId, idempotencyKey, JournalKind.SavingsMove,
            "move to savings", [new(cashAccountId, checked(-amountSen)), new(savingsAccountId, amountSen)]));
    }

    public CommitResult PayBill(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid merchantAccountId, long amountSen, long approvedObligationAmountSen, long transactionLimitSen)
    {
        ValidateAmount(amountSen, transactionLimitSen);
        if (amountSen != approvedObligationAmountSen)
            throw new LedgerValidationException("Payment must match the server-approved obligation amount.");
        return Commit(new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment,
            "approved virtual bill payment", [new(cashAccountId, checked(-amountSen)), new(merchantAccountId, amountSen)]));
    }

    public ObligationResult CreateObligation(Guid runId, Guid merchantAccountId, long amountSen)
    {
        if (runId == Guid.Empty || amountSen <= 0)
            throw new LedgerValidationException("Run ID and positive obligation amount are required.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        LockExistingRun(connection, transaction, runId);
        var kinds = LoadAccountKinds(connection, transaction, runId, [new PostingRequest(merchantAccountId, 1)]);
        if (kinds.Count != 1 || kinds[merchantAccountId] != AccountKind.Merchant)
            throw new LedgerValidationException("Obligation payee must be a merchant in this run.");

        var obligationId = Guid.NewGuid();
        using var command = new NpgsqlCommand(
            "INSERT INTO obligations (id, run_id, merchant_account_id, amount_sen) " +
            "VALUES (@id, @run_id, @merchant_id, @amount_sen)", connection, transaction);
        command.Parameters.AddWithValue("id", obligationId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("merchant_id", merchantAccountId);
        command.Parameters.AddWithValue("amount_sen", amountSen);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new ObligationResult(obligationId, merchantAccountId, amountSen, false);
    }

    public ObligationResult GetObligation(Guid runId, Guid obligationId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT merchant_account_id, amount_sen, paid FROM obligations WHERE id = @id AND run_id = @run_id",
            connection);
        command.Parameters.AddWithValue("id", obligationId);
        command.Parameters.AddWithValue("run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new LedgerValidationException("Obligation not found in this run.");
        return new ObligationResult(obligationId, reader.GetGuid(0), reader.GetInt64(1), reader.GetBoolean(2));
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid obligationId, long transactionLimitSen)
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
        if (!reader.Read()) throw new LedgerValidationException("Obligation not found in this run.");
        var merchantAccountId = reader.GetGuid(0);
        var amountSen = reader.GetInt64(1);
        var isPaid = reader.GetBoolean(2);
        reader.Close();

        var description = $"obligation:{obligationId:N}";
        var request = new JournalRequest(runId, idempotencyKey, JournalKind.BillPayment, description,
            [new(cashAccountId, checked(-amountSen)), new(merchantAccountId, amountSen)]);
        ValidateRequestFields(request);
        var previous = LoadJournalByKey(connection, transaction, runId, idempotencyKey);
        if (previous is not null)
        {
            if (Fingerprint(previous) != Fingerprint(request))
                throw new IdempotencyConflictException("Idempotency key was previously used with a different payload.");
            transaction.Commit();
            return new CommitResult(previous.JournalId, true, true);
        }
        if (isPaid) throw new LedgerValidationException("Obligation has already been paid.");
        ValidateAmount(amountSen, transactionLimitSen);

        var result = CommitWithinTransaction(connection, transaction, request);
        using var update = new NpgsqlCommand(
            "UPDATE obligations SET paid = TRUE, paid_entry_id = @entry_id " +
            "WHERE id = @id AND run_id = @run_id AND paid = FALSE", connection, transaction);
        update.Parameters.AddWithValue("entry_id", result.JournalId);
        update.Parameters.AddWithValue("id", obligationId);
        update.Parameters.AddWithValue("run_id", runId);
        if (update.ExecuteNonQuery() != 1) throw new LedgerValidationException("Obligation payment state changed concurrently.");
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT e.id, e.kind, e.idempotency_key, e.description, p.account_id, p.signed_amount_sen " +
            "FROM journal_entries e JOIN journal_postings p ON p.entry_id = e.id AND p.run_id = e.run_id " +
            "WHERE e.run_id = @run_id ORDER BY e.created_at, e.id, p.id", connection);
        command.Parameters.AddWithValue("run_id", runId);
        using var reader = command.ExecuteReader();
        var journals = new List<LedgerJournal>();
        Guid? currentJournalId = null;
        var currentKind = JournalKind.OpeningFunding;
        var currentKey = string.Empty;
        var currentDescription = string.Empty;
        var postings = new List<LedgerPosting>();

        while (reader.Read())
        {
            var journalId = reader.GetGuid(0);
            if (currentJournalId != journalId)
            {
                if (currentJournalId.HasValue)
                    journals.Add(new LedgerJournal(currentJournalId.Value, runId, currentKey, currentKind, currentDescription, postings));

                currentJournalId = journalId;
                currentKind = FromDatabaseValue<JournalKind>(reader.GetString(1));
                currentKey = reader.GetString(2);
                currentDescription = reader.GetString(3);
                postings = [];
            }

            postings.Add(new LedgerPosting(reader.GetGuid(4), reader.GetInt64(5)));
        }

        if (currentJournalId.HasValue)
            journals.Add(new LedgerJournal(currentJournalId.Value, runId, currentKey, currentKind, currentDescription, postings));
        return journals;
    }

    public CommitResult Commit(JournalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestFields(request);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockExistingRun(connection, transaction, request.RunId);
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
                throw new IdempotencyConflictException("Idempotency key was previously used with a different payload.");
            return new CommitResult(existing.JournalId, true, true);
        }

        var accountKinds = LoadAccountKinds(connection, transaction, request.RunId, request.Postings);
        ValidateJournalShape(request, accountKinds);

        if (request.Kind == JournalKind.OpeningFunding && HasOpeningFunding(connection, transaction, request.RunId))
            throw new LedgerValidationException("Run already has an opening funding journal.");

        var deltas = request.Postings.GroupBy(posting => posting.AccountId)
            .ToDictionary(group => group.Key, group => group.Aggregate(0L, (sum, posting) => checked(sum + posting.AmountSen)));
        foreach (var (accountId, delta) in deltas)
        {
            var kind = accountKinds[accountId];
            if (kind is AccountKind.AgentCash or AccountKind.AgentSavings)
            {
                var balance = GetBalance(connection, transaction, request.RunId, accountId);
                if (checked(balance + delta) < 0)
                    throw new LedgerValidationException("Agent account cannot be overdrawn.");
            }
        }

        var journalId = Guid.NewGuid();
        InsertJournal(connection, transaction, journalId, request.RunId, request.Kind,
            request.IdempotencyKey, request.Description);
        foreach (var posting in request.Postings)
            InsertPosting(connection, transaction, journalId, request.RunId, posting.AccountId, posting.AmountSen);
        return new CommitResult(journalId, true, false);
    }

    private static void ValidateRequestFields(JournalRequest request)
    {
        if (request.RunId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(request.Description))
            throw new LedgerValidationException("Run, idempotency key, and description are required.");
        if (request.Postings is null || request.Postings.Count != 2)
            throw new LedgerValidationException("A journal requires exactly two postings.");
        if (request.Postings.Any(posting => posting.AmountSen == 0 || posting.AccountId == Guid.Empty))
            throw new LedgerValidationException("Postings require non-zero amounts and account IDs.");
        if (checked(request.Postings[0].AmountSen + request.Postings[1].AmountSen) != 0)
            throw new LedgerValidationException("Journal postings must balance to zero.");
        if (request.Postings[0].AccountId == request.Postings[1].AccountId)
            throw new LedgerValidationException("Journal postings must use distinct accounts.");
    }

    private static void ValidateJournalShape(JournalRequest request, IReadOnlyDictionary<Guid, AccountKind> kinds)
    {
        if (kinds.Count != request.Postings.Count)
            throw new LedgerValidationException("Every account must belong to the journal run.");
        var debit = request.Postings.Single(posting => posting.AmountSen < 0);
        var credit = request.Postings.Single(posting => posting.AmountSen > 0);
        var valid = request.Kind switch
        {
            JournalKind.OpeningFunding => kinds[debit.AccountId] == AccountKind.SimulatorTreasury && kinds[credit.AccountId] == AccountKind.AgentCash,
            JournalKind.Transfer => kinds[debit.AccountId] is AccountKind.AgentCash or AccountKind.AgentSavings && kinds[credit.AccountId] is AccountKind.Merchant or AccountKind.AgentCash or AccountKind.AgentSavings,
            JournalKind.SavingsMove => kinds[debit.AccountId] == AccountKind.AgentCash && kinds[credit.AccountId] == AccountKind.AgentSavings,
            JournalKind.BillPayment => kinds[debit.AccountId] == AccountKind.AgentCash && kinds[credit.AccountId] == AccountKind.Merchant,
            _ => false
        };
        if (!valid) throw new LedgerValidationException("Posting account kinds are not permitted for this journal kind.");
    }

    private static void LockExistingRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT id FROM experiment_runs WHERE id = @run_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        if (command.ExecuteScalar() is null)
            throw new LedgerValidationException("Run does not exist.");
    }

    private static IReadOnlyDictionary<Guid, AccountKind> LoadAccountKinds(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid runId, IReadOnlyList<PostingRequest> postings)
    {
        using var command = new NpgsqlCommand(
            "SELECT id, kind FROM accounts WHERE run_id = @run_id AND id = ANY(@account_ids) ORDER BY id FOR UPDATE",
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
        Guid runId, string idempotencyKey)
    {
        using var command = new NpgsqlCommand(
            "SELECT e.id, e.kind, e.description, e.idempotency_key, p.account_id, p.signed_amount_sen " +
            "FROM journal_entries e JOIN journal_postings p ON p.entry_id = e.id AND p.run_id = e.run_id " +
            "WHERE e.run_id = @run_id AND e.idempotency_key = @key ORDER BY p.id",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var journalId = reader.GetGuid(0);
        var kind = FromDatabaseValue<JournalKind>(reader.GetString(1));
        var description = reader.GetString(2);
        var key = reader.GetString(3);
        var postings = new List<LedgerPosting> { new(reader.GetGuid(4), reader.GetInt64(5)) };
        while (reader.Read()) postings.Add(new LedgerPosting(reader.GetGuid(4), reader.GetInt64(5)));
        return new LedgerJournal(journalId, runId, key, kind, description, postings);
    }

    private static string Fingerprint(JournalRequest request) => string.Join("|", request.Kind,
        request.Description, string.Join(";", request.Postings.Select(posting => $"{posting.AccountId:N}:{posting.AmountSen}")));

    private static string Fingerprint(LedgerJournal journal) => string.Join("|", journal.Kind,
        journal.Description, string.Join(";", journal.Postings.Select(posting => $"{posting.AccountId:N}:{posting.AmountSen}")));

    private static void ValidateAmount(long amountSen, long transactionLimitSen)
    {
        if (amountSen <= 0) throw new LedgerValidationException("Amount must be positive.");
        if (transactionLimitSen <= 0 || amountSen > transactionLimitSen)
            throw new LedgerValidationException("Transaction limit exceeded.");
    }

    private static void EnsureRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO experiment_runs (id) VALUES (@run_id) ON CONFLICT (id) DO NOTHING",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
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
        Guid journalId, Guid runId, JournalKind kind, string idempotencyKey, string description)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description) " +
            "VALUES (@id, @run_id, @kind, @key, @description)", connection, transaction);
        command.Parameters.AddWithValue("id", journalId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("kind", ToDatabaseValue(kind));
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("description", description);
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
        _ => throw new LedgerValidationException("Unsupported account kind.")
    };

    private static string ToDatabaseValue(JournalKind kind) => kind switch
    {
        JournalKind.OpeningFunding => "opening_funding",
        JournalKind.Transfer => "transfer",
        JournalKind.SavingsMove => "savings_move",
        JournalKind.BillPayment => "bill_payment",
        _ => throw new LedgerValidationException("Unsupported journal kind.")
    };

    private static T FromDatabaseValue<T>(string value) where T : struct, Enum
    {
        var normalized = string.Concat(value.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (Enum.TryParse<T>(normalized, out var parsed)) return parsed;
        throw new LedgerValidationException("Unsupported value stored in ledger.");
    }
}
