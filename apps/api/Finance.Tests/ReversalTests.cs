using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0006: full, append-only reversal with reversal-aware obligation state.
public sealed class ReversalTests
{
    private sealed record Run(Guid RunId, Guid Cash, Guid Savings, Guid Merchant, ObligationResult Obligation, CommitResult Payment);

    private static Run Setup(IFinanceLedger ledger)
    {
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;
        var savings = ledger.CreateAccount(runId, AccountKind.AgentSavings);
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
        var obligation = ledger.CreateObligation(runId, merchant, 200, "INV-REV-1");
        var payment = ledger.PayObligation(runId, "pay-1", cash, obligation.ObligationId);
        return new Run(runId, cash, savings, merchant, obligation, payment);
    }

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["reversing_payment_restores_money_and_unpays_obligation"] = ledger =>
        {
            var run = Setup(ledger);
            var original = ledger.GetJournals(run.RunId).Single(j => j.JournalId == run.Payment.JournalId);

            var reversal = ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "payment bounced", ReversalInitiator.ScenarioEngine);

            Assert.True(reversal.Committed);
            Assert.True(reversal.Effective);
            Assert.False(reversal.WasIdempotentReplay);
            Assert.Equal(1_000, ledger.GetBalance(run.RunId, run.Cash));
            Assert.Equal(0, ledger.GetBalance(run.RunId, run.Merchant));
            Assert.False(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);

            var journals = ledger.GetJournals(run.RunId);
            Assert.Equal(original, journals.Single(j => j.JournalId == run.Payment.JournalId), JournalComparer.Instance);
            var stored = journals.Single(j => j.JournalId == reversal.JournalId);
            Assert.Equal(JournalKind.Reversal, stored.Kind);
            Assert.Equal(run.Payment.JournalId, stored.ReversesJournalId);
            Assert.Equal(run.Obligation.ObligationId, stored.ObligationId);
            Assert.Equal("payment bounced", stored.ReversalReason);
            Assert.Equal(ReversalInitiator.ScenarioEngine, stored.Initiator);
            AssertReconciled(ledger, run.RunId);
        },
        ["paying_again_with_new_key_after_reversal_pays_once"] = ledger =>
        {
            var run = Setup(ledger);
            ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "researcher correction", ReversalInitiator.Researcher);

            var second = ledger.PayObligation(run.RunId, "pay-2", run.Cash, run.Obligation.ObligationId);

            Assert.False(second.WasIdempotentReplay);
            Assert.True(second.Effective);
            Assert.True(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
            Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
            AssertReconciled(ledger, run.RunId);
        },
        ["old_key_after_reversal_replays_as_not_effective"] = ledger =>
        {
            var run = Setup(ledger);
            var reversal = ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "payment bounced", ReversalInitiator.ScenarioEngine);

            var retry = ledger.PayObligation(run.RunId, "pay-1", run.Cash, run.Obligation.ObligationId);

            Assert.True(retry.Committed);
            Assert.True(retry.WasIdempotentReplay);
            Assert.False(retry.Effective);
            Assert.Equal(reversal.JournalId, retry.ReversedByJournalId);
            Assert.Equal(run.Payment.JournalId, retry.JournalId);
            Assert.Equal(1_000, ledger.GetBalance(run.RunId, run.Cash));
            Assert.False(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
        },
        ["transfer_and_savings_moves_reverse_fully"] = ledger =>
        {
            var run = Setup(ledger);
            var transfer = ledger.Transfer(run.RunId, "t-1", run.Cash, run.Merchant, 100);
            var savings = ledger.MoveToSavings(run.RunId, "s-1", run.Cash, run.Savings, 50);

            ledger.ReverseJournal(run.RunId, "rev-t", transfer.JournalId, "correction", ReversalInitiator.Researcher);
            ledger.ReverseJournal(run.RunId, "rev-s", savings.JournalId, "correction", ReversalInitiator.Researcher);

            Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
            Assert.Equal(0, ledger.GetBalance(run.RunId, run.Savings));
            Assert.Equal(200, ledger.GetBalance(run.RunId, run.Merchant));
            Assert.True(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
            AssertReconciled(ledger, run.RunId);
        },
        ["same_key_replays_and_new_key_cannot_reverse_twice"] = ledger =>
        {
            var run = Setup(ledger);
            var first = ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher);

            var replay = ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher);
            Assert.True(replay.WasIdempotentReplay);
            Assert.True(replay.Effective);
            Assert.Equal(first.JournalId, replay.JournalId);

            AssertCode(LedgerReasonCode.JournalAlreadyReversed, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-2", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.IdempotencyKeyConflict, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "different reason", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.JournalNotReversible, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-3", first.JournalId, "undo the undo", ReversalInitiator.Researcher));
        },
        ["opening_funding_and_unknown_journals_are_rejected"] = ledger =>
        {
            var run = Setup(ledger);
            var opening = ledger.GetJournals(run.RunId).Single(j => j.Kind == JournalKind.OpeningFunding);
            var other = Setup(ledger);

            AssertCode(LedgerReasonCode.JournalNotReversible, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-open", opening.JournalId, "no", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.JournalNotFound, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-x", other.Payment.JournalId, "cross run", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.JournalNotFound, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-y", Guid.NewGuid(), "missing", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.MissingRequiredField, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-z", run.Payment.JournalId, " ", ReversalInitiator.Researcher));
            AssertCode(LedgerReasonCode.UnsupportedInitiatorType, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-z", run.Payment.JournalId, "reason", (ReversalInitiator)9));
            Assert.True(ledger.GetObligation(other.RunId, other.Obligation.ObligationId).Paid);
        },
        ["reversal_cannot_overdraw_agent_account"] = ledger =>
        {
            var run = Setup(ledger);
            var toSavings = ledger.MoveToSavings(run.RunId, "s-1", run.Cash, run.Savings, 300);
            ledger.Transfer(run.RunId, "spend-savings", run.Savings, run.Merchant, 300);

            AssertCode(LedgerReasonCode.InsufficientFunds, ledger, run,
                () => ledger.ReverseJournal(run.RunId, "rev-s", toSavings.JournalId, "correction", ReversalInitiator.Researcher));
        },
    };

    public static TheoryData<string> ScenarioNames() => new(Scenarios.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void InMemory(string scenario) => Scenarios[scenario](new InMemoryLedger());

    [PostgresTheory]
    [MemberData(nameof(ScenarioNames))]
    public void Postgres(string scenario)
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        Scenarios[scenario](new PostgresLedger(dataSource));
    }

    [PostgresFact]
    public void Postgres_restricted_api_role_can_reverse_and_repay()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")
            ?? throw new InvalidOperationException("FINMIND_API_TEST_DATABASE is required.");
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);

        ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher);
        ledger.PayObligation(run.RunId, "pay-2", run.Cash, run.Obligation.ObligationId);

        Assert.True(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
        Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
        AssertReconciled(ledger, run.RunId);
    }

    [PostgresFact]
    public async Task Postgres_concurrent_reversals_produce_exactly_one()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            try
            {
                ledger.ReverseJournal(run.RunId, $"rev-{index}", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher);
                return (LedgerReasonCode?)null;
            }
            catch (LedgerException ex)
            {
                return ex.ReasonCode;
            }
        })));

        Assert.Single(attempts, code => code is null);
        Assert.All(attempts.Where(code => code is not null), code => Assert.Equal(LedgerReasonCode.JournalAlreadyReversed, code));
        Assert.Single(ledger.GetJournals(run.RunId), j => j.Kind == JournalKind.Reversal);
        Assert.Equal(1_000, ledger.GetBalance(run.RunId, run.Cash));
    }

    // The following bypass the application as the owner role to prove the database enforces ADR-0006.

    [PostgresFact]
    public void Database_rejects_reversal_with_mismatched_postings()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);

        var exception = Assert.Throws<PostgresException>(() => RawTransaction(dataSource, (connection, transaction) =>
        {
            var reversalId = InsertReversal(connection, transaction, run, run.Payment.JournalId, run.Obligation.ObligationId);
            InsertPosting(connection, transaction, reversalId, run.RunId, run.Merchant, -150);
            InsertPosting(connection, transaction, reversalId, run.RunId, run.Cash, 150);
            UnpayObligation(connection, transaction, run);
        }));

        Assert.Equal("23514", exception.SqlState);
        Assert.True(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
        Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
    }

    [PostgresFact]
    public void Database_rejects_unpaying_without_reversal()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);

        var exception = Assert.Throws<PostgresException>(() =>
            RawTransaction(dataSource, (connection, transaction) => UnpayObligation(connection, transaction, run)));

        Assert.Equal("23514", exception.SqlState);
        Assert.True(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
    }

    [PostgresFact]
    public void Database_rejects_paying_with_a_reversed_payment()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);
        ledger.ReverseJournal(run.RunId, "rev-1", run.Payment.JournalId, "bounced", ReversalInitiator.Researcher);

        var exception = Assert.Throws<PostgresException>(() => RawTransaction(dataSource, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand(
                "UPDATE obligations SET paid = TRUE, paid_entry_id = @entry WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("entry", run.Payment.JournalId);
            command.Parameters.AddWithValue("id", run.Obligation.ObligationId);
            command.ExecuteNonQuery();
        }));

        Assert.Equal("23514", exception.SqlState);
        Assert.False(ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);
    }

    [PostgresFact]
    public void Database_rejects_second_effective_payment_for_one_obligation()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);

        var exception = Assert.Throws<PostgresException>(() => RawTransaction(dataSource, (connection, transaction) =>
        {
            var paymentId = Guid.NewGuid();
            using var journal = new NpgsqlCommand(
                "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description, obligation_id) " +
                "VALUES (@id, @run_id, 'bill_payment', 'raw-second-payment', 'raw', @obligation_id)", connection, transaction);
            journal.Parameters.AddWithValue("id", paymentId);
            journal.Parameters.AddWithValue("run_id", run.RunId);
            journal.Parameters.AddWithValue("obligation_id", run.Obligation.ObligationId);
            journal.ExecuteNonQuery();
            InsertPosting(connection, transaction, paymentId, run.RunId, run.Cash, -200);
            InsertPosting(connection, transaction, paymentId, run.RunId, run.Merchant, 200);
        }));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
    }

    [PostgresFact]
    public void Database_rejects_reversing_opening_funding()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = Setup(ledger);
        var opening = ledger.GetJournals(run.RunId).Single(j => j.Kind == JournalKind.OpeningFunding);
        var treasury = opening.Postings.Single(p => p.AmountSen < 0).AccountId;

        var exception = Assert.Throws<PostgresException>(() => RawTransaction(dataSource, (connection, transaction) =>
        {
            var reversalId = InsertReversal(connection, transaction, run, opening.JournalId, null);
            InsertPosting(connection, transaction, reversalId, run.RunId, run.Cash, -1_000);
            InsertPosting(connection, transaction, reversalId, run.RunId, treasury, 1_000);
        }));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(800, ledger.GetBalance(run.RunId, run.Cash));
    }

    private static NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);

    private static void RawTransaction(NpgsqlDataSource dataSource, Action<NpgsqlConnection, NpgsqlTransaction> body)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        body(connection, transaction);
        transaction.Commit();
    }

    private static Guid InsertReversal(NpgsqlConnection connection, NpgsqlTransaction transaction, Run run, Guid originalId, Guid? obligationId)
    {
        var reversalId = Guid.NewGuid();
        using var command = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description, reverses_entry_id, obligation_id, reversal_reason, initiator_type) " +
            "VALUES (@id, @run_id, 'reversal', @key, 'raw reversal', @original, @obligation_id, 'raw test', 'researcher')",
            connection, transaction);
        command.Parameters.AddWithValue("id", reversalId);
        command.Parameters.AddWithValue("run_id", run.RunId);
        command.Parameters.AddWithValue("key", $"raw-{reversalId:N}");
        command.Parameters.AddWithValue("original", originalId);
        command.Parameters.AddWithValue("obligation_id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)obligationId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return reversalId;
    }

    private static void UnpayObligation(NpgsqlConnection connection, NpgsqlTransaction transaction, Run run)
    {
        using var command = new NpgsqlCommand(
            "UPDATE obligations SET paid = FALSE, paid_entry_id = NULL WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", run.Obligation.ObligationId);
        command.ExecuteNonQuery();
    }

    private static void InsertPosting(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid entryId, Guid runId, Guid accountId, long amountSen)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO journal_postings (entry_id, run_id, account_id, currency, signed_amount_sen) " +
            "VALUES (@entry_id, @run_id, @account_id, 'MYR', @amount)", connection, transaction);
        command.Parameters.AddWithValue("entry_id", entryId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("amount", amountSen);
        command.ExecuteNonQuery();
    }

    private static void AssertCode(LedgerReasonCode expected, IFinanceLedger ledger, Run run, Action act)
    {
        var before = Snapshot(ledger, run);
        var exception = Assert.ThrowsAny<LedgerException>(act);
        Assert.Equal(expected, exception.ReasonCode);
        Assert.Equal(before, Snapshot(ledger, run));
    }

    private static string Snapshot(IFinanceLedger ledger, Run run) => string.Join("|",
        ledger.GetBalance(run.RunId, run.Cash),
        ledger.GetBalance(run.RunId, run.Savings),
        ledger.GetBalance(run.RunId, run.Merchant),
        ledger.GetJournals(run.RunId).Count,
        ledger.GetObligation(run.RunId, run.Obligation.ObligationId).Paid);

    // Balances equal the sum of immutable postings, and the run's postings sum to zero.
    private static void AssertReconciled(IFinanceLedger ledger, Guid runId)
    {
        var postings = ledger.GetJournals(runId).SelectMany(j => j.Postings).ToArray();
        Assert.Equal(0, postings.Sum(p => p.AmountSen));
        foreach (var account in postings.GroupBy(p => p.AccountId))
            Assert.Equal(account.Sum(p => p.AmountSen), ledger.GetBalance(runId, account.Key));
    }

    private sealed class JournalComparer : IEqualityComparer<LedgerJournal>
    {
        public static readonly JournalComparer Instance = new();
        public bool Equals(LedgerJournal? x, LedgerJournal? y) =>
            x is not null && y is not null
            && (x.JournalId, x.RunId, x.IdempotencyKey, x.Kind, x.Description, x.ObligationId, x.ReversesJournalId, x.ReversalReason, x.Initiator)
                == (y.JournalId, y.RunId, y.IdempotencyKey, y.Kind, y.Description, y.ObligationId, y.ReversesJournalId, y.ReversalReason, y.Initiator)
            && x.Postings.SequenceEqual(y.Postings);
        public int GetHashCode(LedgerJournal obj) => obj.JournalId.GetHashCode();
    }
}
