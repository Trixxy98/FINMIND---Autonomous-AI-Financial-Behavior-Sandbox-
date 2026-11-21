using System.Data;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Finmind.Api.Finance;

public sealed class PostgresLedger(NpgsqlDataSource dataSource) : IFinanceLedger
{
    public Guid CreateAccount(Guid runId, AccountKind kind) =>
        Record(FinanceEvents.Operations.CreateAccount, runId, FinanceEvents.Request(("kind", kind)),
            FinanceEvents.InternalApiSource, () => CreateAccountCore(runId, kind));

    private Guid CreateAccountCore(Guid runId, AccountKind kind)
    {
        if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
        if (!Enum.IsDefined(kind)) throw new LedgerValidationException(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        RunLifecycle.EnsureActive(LockExistingRun(connection, transaction, runId), RunLifecycle.Setup);
        if (GetRunTransactionLimit(connection, transaction, runId) is null)
            throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Run transaction policy is not configured.");

        var accountId = Guid.NewGuid();
        InsertAccount(connection, transaction, runId, accountId, kind);
        AppendEvent(connection, transaction, runId, FinanceEvents.AccountCreated(FinanceEvents.Operations.CreateAccount, accountId, kind));
        transaction.Commit();
        return accountId;
    }

    public OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen) =>
        Record(FinanceEvents.Operations.OpenRun, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("openingBalanceSen", openingBalanceSen), ("transactionLimitSen", transactionLimitSen)),
            FinanceEvents.InternalApiSource, () => OpenRunCore(runId, idempotencyKey, openingBalanceSen, transactionLimitSen));

    private OpenRunResult OpenRunCore(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID and idempotency key are required.");
        if (openingBalanceSen <= 0 || transactionLimitSen <= 0)
            throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Opening balance and transaction limit must be positive sen.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        var runCreated = EnsureRun(connection, transaction, runId, transactionLimitSen);
        // ADR-0008: managed runs are funded only by activation, from their snapshot.
        if (LockRun(connection, transaction, runId) is not null)
            throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Managed runs are funded only by activation.");

        var result = OpenRunWithinTransaction(connection, transaction, runId, idempotencyKey, openingBalanceSen,
            transactionLimitSen, runCreated, FinanceEvents.Operations.OpenRun);
        transaction.Commit();
        return result;
    }

    private static OpenRunResult OpenRunWithinTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen, bool runCreated, string operation)
    {
        var configuredLimit = GetRunTransactionLimit(connection, transaction, runId);
        if (configuredLimit is null)
        {
            if (HasAnyJournal(connection, transaction, runId))
                throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Existing run has ledger activity but no transaction policy; create a new run.");
            SetRunTransactionLimit(connection, transaction, runId, transactionLimitSen);
            configuredLimit = transactionLimitSen;
            runCreated = true;
        }
        else if (configuredLimit.Value != transactionLimitSen)
        {
            throw new IdempotencyConflictException(LedgerReasonCode.RunPolicyConflict, "Run already has a different transaction policy.");
        }

        if (runCreated)
            AppendEvent(connection, transaction, runId, FinanceEvents.RunCreated(transactionLimitSen));

        var existingKey = FindJournalByKey(connection, transaction, runId, idempotencyKey);
        if (existingKey is not null && existingKey.Value != JournalKind.OpeningFunding)
            throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used for a different operation.");

        var existingOpening = FindOpening(connection, transaction, runId);
        if (existingOpening is not null)
        {
            if (existingOpening.Value.IdempotencyKey != idempotencyKey || existingOpening.Value.AmountSen != openingBalanceSen)
                throw new IdempotencyConflictException(LedgerReasonCode.RunPolicyConflict, "Run has already been initialized with a different opening allocation.");

            AppendEvent(connection, transaction, runId, FinanceEvents.LedgerReplayed(
                operation, new CommitResult(existingOpening.Value.JournalId, true, true), idempotencyKey));
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
        AppendEvent(connection, transaction, runId, FinanceEvents.AccountCreated(operation, treasuryId, AccountKind.SimulatorTreasury));
        AppendEvent(connection, transaction, runId, FinanceEvents.AccountCreated(operation, cashId, AccountKind.AgentCash));
        AppendEvent(connection, transaction, runId, FinanceEvents.LedgerCommitted(operation,
            new LedgerJournal(journalId, runId, idempotencyKey, JournalKind.OpeningFunding, "authorized virtual opening allocation",
                [new(treasuryId, negativeAmount), new(cashId, openingBalanceSen)])));

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
        Guid destinationAccountId, long amountSen) =>
        Record(FinanceEvents.Operations.Transfer, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("sourceAccountId", sourceAccountId),
                ("destinationAccountId", destinationAccountId), ("amountSen", amountSen)),
            FinanceEvents.InternalApiSource, () =>
            {
                LedgerRequestValidation.ValidateAmount(amountSen);
                return CommitCore(new JournalRequest(runId, idempotencyKey, JournalKind.Transfer,
                    "virtual transfer", [new(sourceAccountId, checked(-amountSen)), new(destinationAccountId, amountSen)]),
                    FinanceEvents.Operations.Transfer);
            });

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
        Guid savingsAccountId, long amountSen) =>
        Record(FinanceEvents.Operations.MoveToSavings, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("cashAccountId", cashAccountId),
                ("savingsAccountId", savingsAccountId), ("amountSen", amountSen)),
            FinanceEvents.InternalApiSource, () =>
            {
                LedgerRequestValidation.ValidateAmount(amountSen);
                return CommitCore(new JournalRequest(runId, idempotencyKey, JournalKind.SavingsMove,
                    "move to savings", [new(cashAccountId, checked(-amountSen)), new(savingsAccountId, amountSen)]),
                    FinanceEvents.Operations.MoveToSavings);
            });

    public ObligationResult CreateObligation(
        Guid runId,
        Guid merchantAccountId,
        long amountSen,
        string invoiceReference) =>
        Record(FinanceEvents.Operations.CreateObligation, runId,
            FinanceEvents.Request(("merchantAccountId", merchantAccountId), ("amountSen", amountSen), ("invoiceReference", invoiceReference)),
            FinanceEvents.InternalApiSource, () => CreateObligationCore(runId, merchantAccountId, amountSen, invoiceReference));

    private ObligationResult CreateObligationCore(
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

        RunLifecycle.EnsureActive(LockExistingRun(connection, transaction, runId), RunLifecycle.Setup);

        if (GetRunTransactionLimit(connection, transaction, runId) is null)
            throw new LedgerValidationException(
                LedgerReasonCode.RunPolicyNotConfigured,
                "Run transaction policy is not configured.");

        var (obligation, created) = CreateObligationWithinTransaction(connection, transaction, runId,
            merchantAccountId, amountSen, invoiceReference, null, null, null);
        if (created)
            AppendEvent(connection, transaction, runId, FinanceEvents.ObligationCreated(obligation));
        transaction.Commit();
        return obligation;
    }

    // Returns the stored obligation for an identical invoice reference, or inserts a new one.
    private static (ObligationResult Obligation, bool Created) CreateObligationWithinTransaction(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid merchantAccountId, long amountSen,
        string invoiceReference, int? dueDay, string? label, string? scenarioEventKey)
    {
        using (var existingCommand = new NpgsqlCommand(
            """
            SELECT id, merchant_account_id, amount_sen, paid,
                invoice_reference, due_day, label
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
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    NullableString(reader, 6));

                reader.Close();

                if (existing.MerchantAccountId != merchantAccountId
                    || existing.AmountSen != amountSen)
                {
                    throw new IdempotencyConflictException(
                        LedgerReasonCode.InvoiceReferenceConflict,
                        "Invoice reference was previously used with a different payload.");
                }

                return (existing, false);
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
                amount_sen, invoice_reference, due_day, label, scenario_event_key)
            VALUES
                (@id, @run_id, @merchant_id,
                @amount_sen, @invoice_reference, @due_day, @label, @event_key)
            """,
            connection, transaction);

        command.Parameters.AddWithValue("id", obligationId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("merchant_id", merchantAccountId);
        command.Parameters.AddWithValue("amount_sen", amountSen);
        command.Parameters.AddWithValue(
            "invoice_reference", invoiceReference);

        command.Parameters.AddWithValue("due_day", NpgsqlDbType.Integer, (object?)dueDay ?? DBNull.Value);
        command.Parameters.AddWithValue("label", NpgsqlDbType.Text, (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("event_key", NpgsqlDbType.Text, (object?)scenarioEventKey ?? DBNull.Value);
        command.ExecuteNonQuery();
        return (new ObligationResult(obligationId, merchantAccountId, amountSen, false, invoiceReference, dueDay, label), true);
    }

    public ObligationResult GetObligation(
        Guid runId,
        Guid obligationId)
    {
        using var connection = dataSource.OpenConnection();

        using var command = new NpgsqlCommand(
            """
            SELECT merchant_account_id, amount_sen, paid,
                invoice_reference, due_day, label
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
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            NullableString(reader, 5));
    }

    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid obligationId) =>
        Record(FinanceEvents.Operations.PayObligation, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("cashAccountId", cashAccountId), ("obligationId", obligationId)),
            FinanceEvents.InternalApiSource, () => PayObligationCore(runId, idempotencyKey, cashAccountId, obligationId));

    private CommitResult PayObligationCore(Guid runId, string idempotencyKey, Guid cashAccountId,
        Guid obligationId)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        RunLifecycle.EnsureActive(LockExistingRun(connection, transaction, runId), RunLifecycle.MoneyMovement);

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
            AppendEvent(connection, transaction, runId, FinanceEvents.LedgerReplayed(FinanceEvents.Operations.PayObligation, replay, idempotencyKey));
            transaction.Commit();
            return replay;
        }
        if (isPaid) throw new LedgerValidationException(LedgerReasonCode.ObligationAlreadyPaid, "Obligation has already been paid.");

        var result = CommitWithinTransaction(connection, transaction, request, FinanceEvents.Operations.PayObligation);
        using var update = new NpgsqlCommand(
            "UPDATE obligations SET paid = TRUE, paid_entry_id = @entry_id " +
            "WHERE id = @id AND run_id = @run_id AND paid = FALSE", connection, transaction);
        update.Parameters.AddWithValue("entry_id", result.JournalId);
        update.Parameters.AddWithValue("id", obligationId);
        update.Parameters.AddWithValue("run_id", runId);
        if (update.ExecuteNonQuery() != 1) throw new LedgerValidationException(LedgerReasonCode.ConcurrentStateChange, "Obligation payment state changed concurrently.");
        AppendEvent(connection, transaction, runId, FinanceEvents.ObligationPaid(obligationId, result.JournalId, idempotencyKey));
        transaction.Commit();
        return result;
    }

    // ADR-0006: append-only full reversal; obligation returns to UNPAID in the same transaction.
    // Insert order matters: the obligation guard trigger requires the reversal journal to exist first.
    public CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator) =>
        Record(FinanceEvents.Operations.ReverseJournal, runId,
            FinanceEvents.Request(("idempotencyKey", idempotencyKey), ("originalJournalId", originalJournalId), ("reason", reason), ("initiator", initiator)),
            FinanceEvents.SourceFor(initiator), () => ReverseJournalCore(runId, idempotencyKey, originalJournalId, reason, initiator));

    private CommitResult ReverseJournalCore(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator)
    {
        LedgerRequestValidation.ValidateReversal(runId, idempotencyKey, originalJournalId, reason, initiator);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        RunLifecycle.EnsureActive(LockExistingRun(connection, transaction, runId), RunLifecycle.MoneyMovement);

        var existing = LoadJournalByKey(connection, transaction, runId, idempotencyKey);
        if (existing is not null)
        {
            LedgerRequestValidation.EnsureReversalReplay(existing, originalJournalId, reason);
            var replay = ReplayResult(connection, transaction, existing);
            AppendEvent(connection, transaction, runId, FinanceEvents.LedgerReplayed(
                FinanceEvents.Operations.ReverseJournal, replay, idempotencyKey, FinanceEvents.SourceFor(initiator)));
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

        var reversal = new LedgerJournal(reversalId, runId, idempotencyKey, JournalKind.Reversal, $"reversal:{originalJournalId:N}",
            postings.Select(posting => new LedgerPosting(posting.AccountId, posting.AmountSen)).ToArray(),
            original.ObligationId, originalJournalId, reason, initiator);
        AppendEvent(connection, transaction, runId, FinanceEvents.LedgerCommitted(
            FinanceEvents.Operations.ReverseJournal, reversal, FinanceEvents.SourceFor(initiator)));
        AppendEvent(connection, transaction, runId, FinanceEvents.JournalReversed(reversal));
        if (reversal.ObligationId is not null)
            AppendEvent(connection, transaction, runId, FinanceEvents.ObligationPaymentReversed(reversal));
        transaction.Commit();
        return new CommitResult(reversalId, true, false);
    }

    // ADR-0008: create a managed run in DRAFT from a validated, immutable snapshot.
    public RunInfo CreateManagedRun(JsonObject? config, string? codeRevision)
    {
        var runId = Guid.NewGuid();
        Record(FinanceEvents.Operations.CreateRun, runId, new JsonObject(), FinanceEvents.InternalApiSource, () =>
        {
            var snapshot = RunConfiguration.Validate(config, codeRevision);
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            using var insert = new NpgsqlCommand(
                "INSERT INTO experiment_runs (id, transaction_limit_sen, status, config_snapshot) " +
                "VALUES (@id, @limit, 'DRAFT', @snapshot) RETURNING encode(config_hash, 'hex')", connection, transaction);
            insert.Parameters.AddWithValue("id", runId);
            insert.Parameters.AddWithValue("limit", RunConfiguration.TransactionLimit(snapshot));
            insert.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, snapshot.ToJsonString());
            var hash = (string)insert.ExecuteScalar()!;
            AppendEvent(connection, transaction, runId, FinanceEvents.ManagedRunCreated(snapshot, hash));
            transaction.Commit();
            return true;
        });
        return GetRun(runId);
    }

    public RunInfo GetRun(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT status, status_reason, config_snapshot::text, encode(config_hash, 'hex'), queued_at, started_at, finished_at " +
            "FROM experiment_runs WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
        DateTimeOffset? Time(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
        return new RunInfo(runId, !reader.IsDBNull(0), NullableString(reader, 0), NullableString(reader, 1),
            reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2))!.AsObject(), NullableString(reader, 3),
            Time(4), Time(5), Time(6));
    }

    // Lifecycle transitions other than activation. Rejected transitions are recorded as POLICY_DENIED.
    public RunInfo TransitionRun(Guid runId, RunStatus to, string? reason, string source)
    {
        Record(FinanceEvents.Operations.TransitionRun, runId,
            FinanceEvents.Request(("to", RunLifecycle.Wire(to)), ("reason", reason)), source, () =>
            {
                if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
                var storedReason = RunLifecycle.NormalizeReason(to, reason);
                using var connection = dataSource.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var from = LockExistingRun(connection, transaction, runId);
                RunLifecycle.EnsureTransition(from, to);
                if (to == RunStatus.Running)
                    throw new IdempotencyConflictException(LedgerReasonCode.InvalidRunTransition, "Runs enter RUNNING only through activation.");
                UpdateRunStatus(connection, transaction, runId, to, storedReason);
                if (AgentGatewayRules.IsTerminal(to)) RevokeSessions(connection, transaction, runId);
                AppendEvent(connection, transaction, runId,
                    FinanceEvents.RunStatusChanged(FinanceEvents.Operations.TransitionRun, from!.Value, to, storedReason, source));
                transaction.Commit();
                return true;
            });
        return GetRun(runId);
    }

    // INITIALIZING → RUNNING: opening funds and cap come only from the snapshot, in one transaction.
    public OpenRunResult ActivateRun(Guid runId, string source) =>
        Record(FinanceEvents.Operations.ActivateRun, runId, new JsonObject(), source, () =>
        {
            if (runId == Guid.Empty) throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, "Run ID is required.");
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var from = LockExistingRun(connection, transaction, runId);
            RunLifecycle.EnsureTransition(from, RunStatus.Running);
            using var read = new NpgsqlCommand("SELECT config_snapshot::text FROM experiment_runs WHERE id = @id", connection, transaction);
            read.Parameters.AddWithValue("id", runId);
            var snapshot = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
            var scenario = RunConfiguration.ResolveScenario(snapshot);
            using (var clock = new NpgsqlCommand(
                "UPDATE experiment_runs SET virtual_start_date = @start, virtual_day = 0 WHERE id = @id", connection, transaction))
            {
                clock.Parameters.AddWithValue("start", scenario.VirtualStartDate);
                clock.Parameters.AddWithValue("id", runId);
                clock.ExecuteNonQuery();
            }
            var opening = OpenRunWithinTransaction(connection, transaction, runId, RunLifecycle.OpeningIdempotencyKey,
                RunConfiguration.StartingBalance(snapshot), RunConfiguration.TransactionLimit(snapshot), false,
                FinanceEvents.Operations.ActivateRun);

            // ADR-0009: the agent's savings account.
            var savingsId = Guid.NewGuid();
            InsertAccount(connection, transaction, runId, savingsId, AccountKind.AgentSavings);
            AppendEvent(connection, transaction, runId, FinanceEvents.AccountCreated(FinanceEvents.Operations.ActivateRun, savingsId, AccountKind.AgentSavings));

            // ADR-0010: merchants, the stored schedule, and day-0 deliveries commit with opening funding.
            foreach (var merchant in scenario.Merchants)
            {
                var accountId = Guid.NewGuid();
                InsertAccount(connection, transaction, runId, accountId, AccountKind.Merchant, merchant);
                AppendEvent(connection, transaction, runId, FinanceEvents.ScenarioAccountCreated(accountId, merchant));
            }
            var schedule = ScenarioCatalog.Materialize(scenario, RunConfiguration.Seed(snapshot));
            for (var position = 0; position < schedule.Count; position++)
            {
                using var insert = new NpgsqlCommand(
                    "INSERT INTO scenario_events (id, run_id, event_key, position, day, kind, payload) " +
                    "VALUES (@id, @run_id, @key, @position, @day, @kind, @payload)", connection, transaction);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("run_id", runId);
                insert.Parameters.AddWithValue("key", schedule[position].Key);
                insert.Parameters.AddWithValue("position", position);
                insert.Parameters.AddWithValue("day", schedule[position].Day);
                insert.Parameters.AddWithValue("kind", schedule[position].Kind);
                insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, schedule[position].Payload.ToJsonString());
                insert.ExecuteNonQuery();
            }
            AppendEvent(connection, transaction, runId,
                FinanceEvents.ScenarioMaterialized(scenario, schedule.Count, ScenarioCatalog.ScheduleHash(schedule)));
            DeliverDueEvents(connection, transaction, runId, 0, FinanceEvents.Operations.ActivateRun, source);
            UpdateRunStatus(connection, transaction, runId, RunStatus.Running, null);
            AppendEvent(connection, transaction, runId,
                FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ActivateRun, from!.Value, RunStatus.Running, null, source));
            transaction.Commit();
            return opening;
        });

    // ADR-0010: the only way virtual time moves. Delivers due scenario events in schedule order.
    public ClockResult AdvanceClock(Guid runId, int days, string source) =>
        Record(FinanceEvents.Operations.AdvanceClock, runId, FinanceEvents.Request(("days", (long)days)), source, () =>
        {
            if (days < 1) throw new LedgerValidationException(LedgerReasonCode.InvalidAmount, "Days must be positive.");
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var status = LockExistingRun(connection, transaction, runId)
                ?? throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Unmanaged runs have no virtual clock.");
            RunLifecycle.EnsureActive(status, RunLifecycle.MoneyMovement);
            var (start, day, duration) = ReadClock(connection, transaction, runId);
            if (day >= duration)
            {
                transaction.Commit();
                return new ClockResult(day, start.AddDays(day), 0, true, []);
            }

            var newDay = Math.Min(day + days, duration);
            using (var update = new NpgsqlCommand("UPDATE experiment_runs SET virtual_day = @day WHERE id = @id", connection, transaction))
            {
                update.Parameters.AddWithValue("day", newDay);
                update.Parameters.AddWithValue("id", runId);
                update.ExecuteNonQuery();
            }
            AppendEvent(connection, transaction, runId, FinanceEvents.ClockAdvanced(day, newDay, days, source));
            var delivered = DeliverDueEvents(connection, transaction, runId, newDay, FinanceEvents.Operations.AdvanceClock, source);
            transaction.Commit();
            return new ClockResult(newDay, start.AddDays(newDay), duration - newDay, newDay == duration, delivered);
        });

    public EnvironmentView GetEnvironment(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        if (ReadManagedClockStatus(connection, transaction, runId) is null)
            throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Run has no virtual clock.");
        var (start, day, duration) = ReadClock(connection, transaction, runId);
        using var command = new NpgsqlCommand(
            "SELECT event_key, delivered_at_day, payload->>'from', payload->>'text' FROM scenario_events " +
            "WHERE run_id = @run_id AND kind = 'message' AND delivered_at_day IS NOT NULL ORDER BY delivered_at_day, position",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        var messages = new List<EnvironmentMessage>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                messages.Add(new EnvironmentMessage(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }
        transaction.Commit();
        return new EnvironmentView(start.AddDays(day), day, duration - day, messages);
    }

    private static RunStatus? ReadManagedClockStatus(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT coalesce(status, ''), virtual_day IS NOT NULL FROM experiment_runs WHERE id = @run_id", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
        var status = reader.GetString(0);
        return status.Length == 0 || !reader.GetBoolean(1) ? null : RunLifecycle.Parse(status);
    }

    private static (DateOnly Start, int Day, int Duration) ReadClock(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT virtual_start_date, virtual_day, (config_snapshot->>'durationDays')::int FROM experiment_runs WHERE id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("id", runId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetFieldValue<DateOnly>(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    // Delivers undelivered events with day <= upToDay, in (day, file position) order, exactly once.
    private static IReadOnlyList<string> DeliverDueEvents(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, int upToDay, string operation, string source)
    {
        var due = new List<MaterializedScenarioEvent>();
        using (var select = new NpgsqlCommand(
            "SELECT event_key, day, kind, payload::text FROM scenario_events " +
            "WHERE run_id = @run_id AND delivered_at_day IS NULL AND day <= @day ORDER BY day, position", connection, transaction))
        {
            select.Parameters.AddWithValue("run_id", runId);
            select.Parameters.AddWithValue("day", upToDay);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                due.Add(new MaterializedScenarioEvent(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                    JsonNode.Parse(reader.GetString(3))!.AsObject()));
        }

        foreach (var evt in due)
        {
            Guid? obligationId = null;
            if (evt.Kind == "obligation")
            {
                using var merchant = new NpgsqlCommand(
                    "SELECT id FROM accounts WHERE run_id = @run_id AND scenario_key = @key", connection, transaction);
                merchant.Parameters.AddWithValue("run_id", runId);
                merchant.Parameters.AddWithValue("key", evt.Payload["merchant"]!.GetValue<string>());
                var merchantId = (Guid)merchant.ExecuteScalar()!;
                var (obligation, created) = CreateObligationWithinTransaction(connection, transaction, runId, merchantId,
                    evt.Payload["amountSen"]!.GetValue<long>(), evt.Payload["invoiceReference"]!.GetValue<string>(),
                    evt.Payload["dueDay"]!.GetValue<int>(), evt.Payload["description"]!.GetValue<string>(), evt.Key);
                if (created)
                    AppendEvent(connection, transaction, runId, FinanceEvents.ScenarioObligationCreated(obligation, operation, evt.Key, source));
                obligationId = obligation.ObligationId;
            }

            using var mark = new NpgsqlCommand(
                "UPDATE scenario_events SET delivered_at_day = @day WHERE run_id = @run_id AND event_key = @key", connection, transaction);
            mark.Parameters.AddWithValue("day", upToDay);
            mark.Parameters.AddWithValue("run_id", runId);
            mark.Parameters.AddWithValue("key", evt.Key);
            mark.ExecuteNonQuery();
            AppendEvent(connection, transaction, runId, FinanceEvents.ScenarioEventDelivered(operation, evt, upToDay, obligationId, source));
        }
        return due.Select(e => e.Key).ToArray();
    }

    public RunInfo? ClaimQueuedRun(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        Guid runId;
        using (var connection = dataSource.OpenConnection())
        using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            using var select = new NpgsqlCommand(
                "SELECT id FROM experiment_runs WHERE status = 'QUEUED' ORDER BY queued_at, id LIMIT 1 FOR UPDATE SKIP LOCKED",
                connection, transaction);
            if (select.ExecuteScalar() is not Guid claimed)
            {
                transaction.Commit();
                return null;
            }
            runId = claimed;
            UpdateRunStatus(connection, transaction, runId, RunStatus.Initializing, null);
            RevokeSessions(connection, transaction, runId);
            using var insert = new NpgsqlCommand(
                "INSERT INTO run_sessions (id, run_id, token_hash, expires_at) VALUES (@id, @run_id, @hash, @expires)",
                connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("run_id", runId);
            insert.Parameters.AddWithValue("hash", tokenHash);
            insert.Parameters.AddWithValue("expires", expiresAt);
            insert.ExecuteNonQuery();
            AppendEvent(connection, transaction, runId, FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ClaimRun,
                RunStatus.Queued, RunStatus.Initializing, null, FinanceEvents.WorkerSource));
            transaction.Commit();
        }
        return GetRun(runId);
    }

    public Guid? ResolveSession(byte[] tokenHash, DateTimeOffset now, DateTimeOffset newExpiresAt)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "UPDATE run_sessions SET expires_at = @new_expires WHERE token_hash = @hash AND revoked_at IS NULL " +
            "AND expires_at > @now RETURNING run_id", connection);
        command.Parameters.AddWithValue("hash", tokenHash);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("new_expires", newExpiresAt);
        return command.ExecuteScalar() as Guid?;
    }

    private static void RevokeSessions(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "UPDATE run_sessions SET revoked_at = now() WHERE run_id = @run_id AND revoked_at IS NULL", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.ExecuteNonQuery();
    }

    // Records the call and TOOL_CALL_PROPOSED under the run lock, then checks status and the action budget.
    public ToolCallBegin BeginToolCall(Guid runId, string toolCallId, string tool, JsonObject redactedArguments, string requestHash)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        var status = LockExistingRun(connection, transaction, runId);

        using (var existing = new NpgsqlCommand(
            "SELECT request_hash, response::text FROM tool_calls WHERE run_id = @run_id AND tool_call_id = @id", connection, transaction))
        {
            existing.Parameters.AddWithValue("run_id", runId);
            existing.Parameters.AddWithValue("id", toolCallId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var storedHash = reader.GetString(0);
                var response = NullableString(reader, 1);
                reader.Close();
                transaction.Commit();
                if (storedHash != requestHash) return new ToolCallBegin(ToolCallBeginKind.Conflict, null, null);
                if (response is not null) return new ToolCallBegin(ToolCallBeginKind.Replay, JsonNode.Parse(response)!.AsObject(), null);
                return new ToolCallBegin(ToolCallBeginKind.Resume, null, status == RunStatus.Running ? null : LedgerReasonCode.RunNotActive);
            }
        }

        long counted;
        using (var count = new NpgsqlCommand("SELECT count(*) FROM tool_calls WHERE run_id = @run_id", connection, transaction))
        {
            count.Parameters.AddWithValue("run_id", runId);
            counted = (long)count.ExecuteScalar()!;
        }
        using (var insert = new NpgsqlCommand(
            "INSERT INTO tool_calls (id, run_id, tool_call_id, tool_name, arguments_redacted, request_hash) " +
            "VALUES (@id, @run_id, @call_id, @tool, @arguments, @hash)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("run_id", runId);
            insert.Parameters.AddWithValue("call_id", toolCallId);
            insert.Parameters.AddWithValue("tool", tool);
            insert.Parameters.AddWithValue("arguments", NpgsqlDbType.Jsonb, redactedArguments.ToJsonString());
            insert.Parameters.AddWithValue("hash", requestHash);
            insert.ExecuteNonQuery();
        }
        AppendEvent(connection, transaction, runId, FinanceEvents.ToolCallProposed(toolCallId, tool, redactedArguments));

        LedgerReasonCode? denial = null;
        if (status != RunStatus.Running)
        {
            denial = LedgerReasonCode.RunNotActive;
        }
        else if (WallTimeExceeded(connection, transaction, runId))
        {
            denial = Exhaust(connection, transaction, runId, LedgerReasonCode.WallTimeBudgetExceeded,
                AgentGatewayRules.WallTimeExhaustedReason, toolCallId);
        }
        else
        {
            using var read = new NpgsqlCommand("SELECT config_snapshot::text FROM experiment_runs WHERE id = @id", connection, transaction);
            read.Parameters.AddWithValue("id", runId);
            var snapshot = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
            if (counted >= AgentGatewayRules.MaxActions(snapshot))
                denial = Exhaust(connection, transaction, runId, LedgerReasonCode.ActionBudgetExceeded,
                    AgentGatewayRules.BudgetExhaustedReason, toolCallId);
        }
        transaction.Commit();
        return new ToolCallBegin(ToolCallBeginKind.New, null, denial);
    }

    // Database time is the wall clock for budgets, so all API instances agree.
    private static bool WallTimeExceeded(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT coalesce(started_at + make_interval(secs => (config_snapshot->>'maxWallTimeSeconds')::int) <= now(), false) " +
            "FROM experiment_runs WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", runId);
        return (bool)command.ExecuteScalar()!;
    }

    private static LedgerReasonCode Exhaust(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId,
        LedgerReasonCode code, string reason, string traceId)
    {
        UpdateRunStatus(connection, transaction, runId, RunStatus.BudgetExceeded, reason);
        RevokeSessions(connection, transaction, runId);
        AppendEvent(connection, transaction, runId, FinanceEvents.RunStatusChanged(FinanceEvents.Operations.ToolCall,
            RunStatus.Running, RunStatus.BudgetExceeded, reason, FinanceEvents.GatewaySource) with { TraceId = traceId });
        return code;
    }

    // ADR-0011: records one agent turn under the run lock. A retried turn is not recorded or counted twice.
    public ModelInvocationResult RecordModelInvocation(Guid runId, ModelInvocation invocation)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        var status = LockExistingRun(connection, transaction, runId);
        var trace = FinanceEvents.TurnTrace(invocation.Turn);

        long used, cost;
        JsonObject snapshot;
        using (var read = new NpgsqlCommand(
            "SELECT model_tokens_used, model_cost_micro_usd, config_snapshot::text, " +
            "EXISTS (SELECT 1 FROM run_events WHERE run_id = @id AND event_type = 'MODEL_INVOCATION_COMPLETED' AND trace_id = @trace) " +
            "FROM experiment_runs WHERE id = @id", connection, transaction))
        {
            read.Parameters.AddWithValue("id", runId);
            read.Parameters.AddWithValue("trace", trace);
            using var reader = read.ExecuteReader();
            reader.Read();
            used = reader.GetInt64(0);
            cost = reader.GetInt64(1);
            snapshot = reader.IsDBNull(2) ? new JsonObject() : JsonNode.Parse(reader.GetString(2))!.AsObject();
            if (reader.GetBoolean(3))
            {
                reader.Close();
                transaction.Commit();
                return new ModelInvocationResult(null, true, used, cost);
            }
        }
        if (status != RunStatus.Running)
        {
            transaction.Commit();
            return new ModelInvocationResult(LedgerReasonCode.RunNotActive, false, used, cost);
        }

        var turnCost = AgentGatewayRules.CostOf(snapshot, invocation);
        AppendEvent(connection, transaction, runId, FinanceEvents.ModelInvocationCompleted(invocation, turnCost));
        used = checked(used + invocation.BudgetTokens);
        cost = checked(cost + turnCost);
        using (var update = new NpgsqlCommand(
            "UPDATE experiment_runs SET model_tokens_used = @used, model_cost_micro_usd = @cost WHERE id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("used", used);
            update.Parameters.AddWithValue("cost", cost);
            update.Parameters.AddWithValue("id", runId);
            update.ExecuteNonQuery();
        }

        LedgerReasonCode? denial = null;
        if (WallTimeExceeded(connection, transaction, runId))
            denial = Exhaust(connection, transaction, runId, LedgerReasonCode.WallTimeBudgetExceeded, AgentGatewayRules.WallTimeExhaustedReason, trace);
        else if (used > AgentGatewayRules.MaxModelTokens(snapshot))
            denial = Exhaust(connection, transaction, runId, LedgerReasonCode.TokenBudgetExceeded, AgentGatewayRules.TokenExhaustedReason, trace);
        else if (AgentGatewayRules.Provider(snapshot) != "mock" && cost > AgentGatewayRules.MaxCostMicroUsd(snapshot))
            denial = Exhaust(connection, transaction, runId, LedgerReasonCode.CostBudgetExceeded, AgentGatewayRules.CostExhaustedReason, trace);
        transaction.Commit();
        return new ModelInvocationResult(denial, false, used, cost);
    }

    // Stores the result and TOOL_RETURNED (plus any agent event such as FUNDING_REQUESTED) in one transaction.
    public void CompleteToolCall(Guid runId, string toolCallId, string tool, ToolCallCompletion completion)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockExistingRun(connection, transaction, runId);
        using var update = new NpgsqlCommand(
            "UPDATE tool_calls SET outcome = @outcome, reason_code = @reason, journal_id = @journal, response = @response, " +
            "completed_at = now() WHERE run_id = @run_id AND tool_call_id = @id AND completed_at IS NULL", connection, transaction);
        update.Parameters.AddWithValue("outcome", completion.Status);
        update.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)completion.ReasonCode ?? DBNull.Value);
        update.Parameters.AddWithValue("journal", NpgsqlDbType.Uuid, (object?)completion.JournalId ?? DBNull.Value);
        update.Parameters.AddWithValue("response", NpgsqlDbType.Jsonb, completion.Response.ToJsonString());
        update.Parameters.AddWithValue("run_id", runId);
        update.Parameters.AddWithValue("id", toolCallId);
        if (update.ExecuteNonQuery() == 1)
        {
            if (completion.ExtraEvent is { } extra) AppendEvent(connection, transaction, runId, extra);
            AppendEvent(connection, transaction, runId,
                FinanceEvents.ToolReturned(toolCallId, tool, completion.Status, completion.ReasonCode, completion.JournalId));
        }
        transaction.Commit();
    }

    public AgentView GetAgentView(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        Guid? cash = null, savings = null;
        var merchants = new List<AgentMerchant>();
        using (var accounts = new NpgsqlCommand("SELECT id, kind, scenario_key, label FROM accounts WHERE run_id = @run_id", connection))
        {
            accounts.Parameters.AddWithValue("run_id", runId);
            using var reader = accounts.ExecuteReader();
            while (reader.Read())
            {
                var kind = FromDatabaseValue<AccountKind>(reader.GetString(1));
                if (kind == AccountKind.AgentCash) cash = reader.GetGuid(0);
                else if (kind == AccountKind.AgentSavings) savings ??= reader.GetGuid(0);
                else if (!reader.IsDBNull(2)) merchants.Add(new AgentMerchant(reader.GetGuid(0), reader.GetString(2), reader.GetString(3)));
            }
        }
        if (cash is null) throw new LedgerValidationException(LedgerReasonCode.RunNotActive, "Run has no agent account.");

        var obligations = new List<ObligationResult>();
        using (var command = new NpgsqlCommand(
            "SELECT id, merchant_account_id, amount_sen, paid, invoice_reference, due_day, label FROM obligations WHERE run_id = @run_id",
            connection))
        {
            command.Parameters.AddWithValue("run_id", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                obligations.Add(new ObligationResult(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetBoolean(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), NullableString(reader, 6)));
        }
        return new AgentView(cash.Value, savings, merchants.OrderBy(m => m.Key, StringComparer.Ordinal).ToArray(),
            AgentGatewayRules.Order(obligations));
    }

    private static void UpdateRunStatus(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, RunStatus to, string? reason)
    {
        using var update = new NpgsqlCommand(
            "UPDATE experiment_runs SET status = @status, status_reason = @reason WHERE id = @id", connection, transaction);
        update.Parameters.AddWithValue("status", RunLifecycle.Wire(to));
        update.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)reason ?? DBNull.Value);
        update.Parameters.AddWithValue("id", runId);
        update.ExecuteNonQuery();
    }

    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        return LoadJournals(connection, null, runId, string.Empty, _ => { });
    }

    public IReadOnlyList<RunEvent> GetEvents(Guid runId, long afterSequence, int limit)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT sequence, event_type, event_version, occurred_at, source, operation, outcome, reason_code, " +
            "idempotency_key, journal_id, obligation_id, payload::text, encode(event_hash, 'hex'), virtual_time, trace_id " +
            "FROM run_events WHERE run_id = @run_id AND sequence > @after ORDER BY sequence LIMIT @limit", connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, RunEventPaging.MaxLimit));
        using var reader = command.ExecuteReader();
        var events = new List<RunEvent>();
        while (reader.Read())
        {
            events.Add(new RunEvent(
                runId, reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4),
                NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetGuid(10),
                JsonNode.Parse(reader.GetString(11))!.AsObject(), reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13), NullableString(reader, 14)));
        }
        return events;
    }

    // ADR-0007: first sequence whose position, link, or hash does not match, or null if the chain is intact.
    public long? VerifyEventChain(Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT finmind_verify_run_events(@run_id)", connection);
        command.Parameters.AddWithValue("run_id", runId);
        return command.ExecuteScalar() as long?;
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // ADR-0007: successful operations append their events in their own transaction (see *Core methods).
    // Rejections roll back, then append POLICY_DENIED in an event-only transaction.
    // No decision is returned unless it is recorded.
    private T Record<T>(string operation, Guid runId, JsonObject request, string source, Func<T> body)
    {
        try
        {
            return LedgerRequestValidation.TranslateOverflow(body);
        }
        catch (LedgerException exception)
        {
            if (FinanceEvents.IsRecordable(runId, exception))
                AppendEventOnly(runId, FinanceEvents.PolicyDenied(operation, exception, request, source), required: true);
            throw;
        }
        catch (Exception)
        {
            AppendEventOnly(runId, FinanceEvents.FinanceError(operation, LedgerReasonCode.InternalError,
                request["idempotencyKey"]?.GetValue<string>(), source), required: false);
            throw;
        }
    }

    private void AppendEventOnly(Guid runId, PendingEvent pending, bool required)
    {
        if (runId == Guid.Empty) return;
        try
        {
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            using var exists = new NpgsqlCommand("SELECT 1 FROM experiment_runs WHERE id = @run_id", connection, transaction);
            exists.Parameters.AddWithValue("run_id", runId);
            if (exists.ExecuteScalar() is null) return;
            AppendEvent(connection, transaction, runId, pending);
            transaction.Commit();
        }
        catch (Exception) when (required)
        {
            throw new LedgerValidationException(LedgerReasonCode.InternalError, "The decision could not be recorded.");
        }
        catch (Exception)
        {
            // FINANCE_ERROR is best effort (ADR-0007); the original failure is rethrown by the caller.
        }
    }

    // Sequence, prev_hash, occurred_at, and event_hash are assigned by the database trigger.
    private static void AppendEvent(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, PendingEvent pending)
    {
        pending = pending.WithCallContext();
        using var command = new NpgsqlCommand(
            "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, operation, " +
            "outcome, reason_code, idempotency_key, journal_id, obligation_id, payload, event_hash, trace_id) " +
            "VALUES (@id, @run_id, 0, @event_type, @event_version, now(), @source, @operation, @outcome, @reason_code, " +
            "@idempotency_key, @journal_id, @obligation_id, @payload, '\\x00'::bytea, @trace_id)", connection, transaction);
        command.Parameters.AddWithValue("trace_id", NpgsqlDbType.Text, (object?)pending.TraceId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("event_type", pending.EventType);
        command.Parameters.AddWithValue("event_version", pending.EventVersion);
        command.Parameters.AddWithValue("source", pending.Source);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, (object?)pending.Operation ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Text, (object?)pending.Outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("reason_code", NpgsqlDbType.Text, (object?)pending.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, (object?)pending.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("journal_id", NpgsqlDbType.Uuid, (object?)pending.JournalId ?? DBNull.Value);
        command.Parameters.AddWithValue("obligation_id", NpgsqlDbType.Uuid, (object?)pending.ObligationId ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, pending.Payload.ToJsonString());
        command.ExecuteNonQuery();
    }

    public CommitResult Commit(JournalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Record(FinanceEvents.Operations.Commit, request.RunId,
            FinanceEvents.Request(("idempotencyKey", request.IdempotencyKey), ("kind", request.Kind)),
            FinanceEvents.InternalApiSource, () => CommitCore(request, FinanceEvents.Operations.Commit));
    }

    private CommitResult CommitCore(JournalRequest request, string operation)
    {
        ValidateRequestFields(request);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        RunLifecycle.EnsureActive(LockExistingRun(connection, transaction, request.RunId), RunLifecycle.MoneyMovement);
        if (GetRunTransactionLimit(connection, transaction, request.RunId) is null)
            throw new LedgerValidationException(LedgerReasonCode.RunPolicyNotConfigured, "Run transaction policy is not configured.");
        var result = CommitWithinTransaction(connection, transaction, request, operation);
        transaction.Commit();
        return result;
    }

    private static CommitResult CommitWithinTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
        JournalRequest request, string operation)
    {
        var existing = LoadJournalByKey(connection, transaction, request.RunId, request.IdempotencyKey);
        if (existing is not null)
        {
            if (Fingerprint(existing) != Fingerprint(request))
                throw new IdempotencyConflictException(LedgerReasonCode.IdempotencyKeyConflict, "Idempotency key was previously used with a different payload.");
            var replay = ReplayResult(connection, transaction, existing);
            AppendEvent(connection, transaction, request.RunId, FinanceEvents.LedgerReplayed(operation, replay, request.IdempotencyKey));
            return replay;
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
        AppendEvent(connection, transaction, request.RunId, FinanceEvents.LedgerCommitted(operation,
            new LedgerJournal(journalId, request.RunId, request.IdempotencyKey, request.Kind, request.Description,
                request.Postings.Select(posting => new LedgerPosting(posting.AccountId, posting.AmountSen)).ToArray(), request.ObligationId)));
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

    // Locks the run row and returns its lifecycle status (null for unmanaged legacy runs).
    private static RunStatus? LockExistingRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT coalesce(status, '') FROM experiment_runs WHERE id = @run_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        var status = command.ExecuteScalar() as string
            ?? throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
        return status.Length == 0 ? null : RunLifecycle.Parse(status);
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


    // Returns true when this call created the run.
    private static bool EnsureRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid runId, long transactionLimitSen)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO experiment_runs (id, transaction_limit_sen) VALUES (@run_id, @transaction_limit_sen) " +
            "ON CONFLICT (id) DO NOTHING",
            connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("transaction_limit_sen", transactionLimitSen);
        return command.ExecuteNonQuery() == 1;
    }

    private static RunStatus? LockRun(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = new NpgsqlCommand(
            "SELECT coalesce(status, '') FROM experiment_runs WHERE id = @run_id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        var status = (string?)command.ExecuteScalar() ?? string.Empty;
        return status.Length == 0 ? null : RunLifecycle.Parse(status);
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
        Guid runId, Guid accountId, AccountKind kind, ScenarioMerchant? merchant = null)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO accounts (id, run_id, kind, scenario_key, label) VALUES (@id, @run_id, @kind, @scenario_key, @label)",
            connection, transaction);
        command.Parameters.AddWithValue("scenario_key", NpgsqlDbType.Text, (object?)merchant?.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("label", NpgsqlDbType.Text, (object?)merchant?.Label ?? DBNull.Value);
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
