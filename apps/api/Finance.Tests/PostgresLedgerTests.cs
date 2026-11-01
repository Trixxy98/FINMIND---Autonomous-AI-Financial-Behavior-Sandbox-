using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

public sealed class PostgresLedgerTests
{
    [PostgresFact]
    public void Opening_run_and_retry_survive_ledger_recreation()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        var runId = Guid.NewGuid();
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);

        var first = ledger.OpenRun(runId, "open-1", 25_000, 500);
        var restartedLedger = new PostgresLedger(dataSource);
        var retry = restartedLedger.OpenRun(runId, "open-1", 25_000, 500);

        Assert.Equal(first.CashAccountId, retry.CashAccountId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.Equal(25_000, restartedLedger.GetBalance(runId, first.CashAccountId));
    }

    [PostgresFact]
    public async Task Concurrent_transfers_are_idempotent_durable_and_cannot_overdraw()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runId = Guid.NewGuid();
        var ledger = new PostgresLedger(dataSource);
        var cashId = ledger.OpenRun(runId, "open", 100, 100).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var first = ledger.Transfer(runId, "first-transfer", cashId, merchantId, 10);
        var replay = ledger.Transfer(runId, "first-transfer", cashId, merchantId, 10);

        Assert.Equal(first.JournalId, replay.JournalId);
        Assert.True(replay.WasIdempotentReplay);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
        {
            try
            {
                ledger.Transfer(runId, $"concurrent-{index}", cashId, merchantId, 10);
                return true;
            }
            catch (LedgerValidationException)
            {
                return false;
            }
        })));

        Assert.Equal(9, results.Count(result => result));
        var restartedLedger = new PostgresLedger(dataSource);
        Assert.Equal(0, restartedLedger.GetBalance(runId, cashId));
        Assert.Equal(100, restartedLedger.GetBalance(runId, merchantId));
        var journals = restartedLedger.GetJournals(runId);
        Assert.Equal(11, journals.Count);
        Assert.Equal(JournalKind.OpeningFunding, journals[0].Kind);
        Assert.All(journals, journal => Assert.Equal(0, journal.Postings.Sum(posting => posting.AmountSen)));
    }

    [PostgresFact]
    public void Obligation_payment_is_atomic_run_scoped_and_idempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runId = Guid.NewGuid();
        var ledger = new PostgresLedger(dataSource);
        var cashId = ledger.OpenRun(runId, "open", 500, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var obligation = ledger.CreateObligation(runId, merchantId, 120, "INV-PAYMENT-001");

        var payment = ledger.PayObligation(runId, "bill-1", cashId, obligation.ObligationId);
        var retry = ledger.PayObligation(runId, "bill-1", cashId, obligation.ObligationId);

        Assert.Equal(payment.JournalId, retry.JournalId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.True(new PostgresLedger(dataSource).GetObligation(runId, obligation.ObligationId).Paid);
        Assert.Equal(380, ledger.GetBalance(runId, cashId));
        Assert.Equal(120, ledger.GetBalance(runId, merchantId));
        Assert.Throws<LedgerValidationException>(() => ledger.PayObligation(runId, "bill-2", cashId, obligation.ObligationId));
    }

    [PostgresFact]
    public void Savings_and_bill_actions_enforce_caps_and_keep_denials_side_effect_free()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runId = Guid.NewGuid();
        var ledger = new PostgresLedger(dataSource);
        var cashId = ledger.OpenRun(runId, "open", 1_000, 100).CashAccountId;
        var savingsId = ledger.CreateAccount(runId, AccountKind.AgentSavings);
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);

        Assert.Throws<LedgerValidationException>(() => ledger.MoveToSavings(runId, "savings-over-cap", cashId, savingsId, 101));
        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, savingsId));

        var savingsMove = ledger.MoveToSavings(runId, "savings-at-cap", cashId, savingsId, 100);
        var savingsRetry = ledger.MoveToSavings(runId, "savings-at-cap", cashId, savingsId, 100);
        Assert.Equal(savingsMove.JournalId, savingsRetry.JournalId);
        Assert.True(savingsRetry.WasIdempotentReplay);

        var overCapBill = ledger.CreateObligation(runId, merchantId, 101, "INV-OVER-CAP-001");
        Assert.Throws<LedgerValidationException>(() => ledger.PayObligation(runId, "bill-over-cap", cashId, overCapBill.ObligationId));
        Assert.False(ledger.GetObligation(runId, overCapBill.ObligationId).Paid);
        Assert.Equal(900, ledger.GetBalance(runId, cashId));
        Assert.Equal(100, ledger.GetBalance(runId, savingsId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Equal(2, ledger.GetJournals(runId).Count);

        var bill = ledger.CreateObligation(runId, merchantId, 100, "INV-AT-CAP-001");
        var payment = ledger.PayObligation(runId, "bill-at-cap", cashId, bill.ObligationId);
        var paymentRetry = ledger.PayObligation(runId, "bill-at-cap", cashId, bill.ObligationId);
        Assert.Equal(payment.JournalId, paymentRetry.JournalId);
        Assert.True(paymentRetry.WasIdempotentReplay);
        Assert.True(ledger.GetObligation(runId, bill.ObligationId).Paid);
        Assert.Equal(800, ledger.GetBalance(runId, cashId));
        Assert.Equal(100, ledger.GetBalance(runId, savingsId));
        Assert.Equal(100, ledger.GetBalance(runId, merchantId));
        Assert.Equal(3, ledger.GetJournals(runId).Count);
    }

    [PostgresFact]
    public async Task Concurrent_opening_funding_is_single_and_cross_run_spending_is_denied()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => ledger.OpenRun(runId, "shared-open", 1_000, 200))));

        Assert.Single(results.Select(result => result.CashAccountId).Distinct());
        Assert.Single(ledger.GetJournals(runId));

        var otherRunId = Guid.NewGuid();
        var otherCash = ledger.OpenRun(otherRunId, "other-open", 1_000, 200).CashAccountId;
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(runId, "cross-run", otherCash, merchant, 10));
        Assert.Equal(1_000, ledger.GetBalance(otherRunId, otherCash));
        Assert.Equal(0, ledger.GetBalance(runId, merchant));
    }

    [PostgresFact]
    public void Transaction_cap_is_stored_on_run_and_not_overridable_by_action_request()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runId = Guid.NewGuid();
        var ledger = new PostgresLedger(dataSource);
        var cashId = ledger.OpenRun(runId, "open", 1_000, 100).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);

        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(runId, "over-cap", cashId, merchantId, 101));
        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));

        ledger.Transfer(runId, "at-cap", cashId, merchantId, 100);
        Assert.Throws<IdempotencyConflictException>(() => ledger.OpenRun(runId, "open", 1_000, 101));

        using var updateConnection = dataSource.OpenConnection();
        using var update = new NpgsqlCommand(
            "UPDATE experiment_runs SET transaction_limit_sen = 101 WHERE id = @run_id", updateConnection);
        update.Parameters.AddWithValue("run_id", runId);
        var exception = Assert.Throws<PostgresException>(() => update.ExecuteNonQuery());
        Assert.Equal("55000", exception.SqlState);
    }

    [PostgresFact]
    public void Legacy_run_with_ledger_activity_cannot_be_reopened_with_a_new_policy()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        var journalId = Guid.NewGuid();

        using (var connection = dataSource.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var run = new NpgsqlCommand("INSERT INTO experiment_runs (id) VALUES (@run_id)", connection, transaction);
            run.Parameters.AddWithValue("run_id", runId);
            run.ExecuteNonQuery();

            using var accounts = new NpgsqlCommand(
                "INSERT INTO accounts (id, run_id, kind) VALUES (@cash_id, @run_id, 'agent_cash'), (@merchant_id, @run_id, 'agent_savings')",
                connection, transaction);
            accounts.Parameters.AddWithValue("cash_id", cashId);
            accounts.Parameters.AddWithValue("run_id", runId);
            accounts.Parameters.AddWithValue("merchant_id", merchantId);
            accounts.ExecuteNonQuery();

            using var journal = new NpgsqlCommand(
                "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description) " +
                "VALUES (@journal_id, @run_id, 'transfer', 'legacy-transfer', 'legacy activity')",
                connection, transaction);
            journal.Parameters.AddWithValue("journal_id", journalId);
            journal.Parameters.AddWithValue("run_id", runId);
            journal.ExecuteNonQuery();

            using var postings = new NpgsqlCommand(
                "INSERT INTO journal_postings (entry_id, run_id, account_id, currency, signed_amount_sen) " +
                "VALUES (@journal_id, @run_id, @cash_id, 'MYR', 10), (@journal_id, @run_id, @merchant_id, 'MYR', -10)",
                connection, transaction);
            postings.Parameters.AddWithValue("journal_id", journalId);
            postings.Parameters.AddWithValue("run_id", runId);
            postings.Parameters.AddWithValue("cash_id", cashId);
            postings.Parameters.AddWithValue("merchant_id", merchantId);
            postings.ExecuteNonQuery();
            transaction.Commit();
        }

        var ledger = new PostgresLedger(dataSource);
        Assert.Throws<LedgerValidationException>(() => ledger.OpenRun(runId, "new-opening", 500, 100));
        Assert.Single(ledger.GetJournals(runId));
        Assert.Equal(10, ledger.GetBalance(runId, cashId));
    }

    [PostgresFact]
    public void Duplicate_invoice_has_one_durable_obligation_and_one_payment()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FINMIND_TEST_DATABASE")!;

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(
            runId, "open", 1_000, 200).CashAccountId;

        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var otherMerchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var first = ledger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        var restartedLedger = new PostgresLedger(dataSource);
        var retry = restartedLedger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        Assert.Equal(first, retry);

        Assert.Throws<IdempotencyConflictException>(() =>
            ledger.CreateObligation(
                runId, merchantId, 121, "INV-DUPLICATE-001"));

        Assert.Throws<IdempotencyConflictException>(() =>
            ledger.CreateObligation(
                runId, otherMerchantId, 120, "INV-DUPLICATE-001"));

        Assert.Equal(first, ledger.GetObligation(runId, first.ObligationId));
        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Single(ledger.GetJournals(runId));

        var payment = ledger.PayObligation(
            runId, "payment", cashId, first.ObligationId);

        var afterPayment = restartedLedger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        Assert.Equal(first.ObligationId, afterPayment.ObligationId);
        Assert.True(afterPayment.Paid);

        var paymentRetry = restartedLedger.PayObligation(
            runId, "payment", cashId, afterPayment.ObligationId);

        Assert.Equal(payment.JournalId, paymentRetry.JournalId);
        Assert.True(paymentRetry.WasIdempotentReplay);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayObligation(
                runId, "second-payment", cashId,
                afterPayment.ObligationId));

        Assert.Equal(880, ledger.GetBalance(runId, cashId));
        Assert.Equal(120, ledger.GetBalance(runId, merchantId));
        Assert.Equal(0, ledger.GetBalance(runId, otherMerchantId));
        Assert.Equal(2, ledger.GetJournals(runId).Count);
    }

    [PostgresFact]
    public async Task Concurrent_invoice_delivery_persists_one_obligation()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FINMIND_TEST_DATABASE")!;

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(
            runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var requests = Enumerable.Range(0, 12).Select(index =>
            Task.Run(() => new PostgresLedger(dataSource).CreateObligation(
                runId, merchantId, 120, "INV-CONCURRENT-001")));

        var results = await Task.WhenAll(requests);

        Assert.Single(results.Select(result => result.ObligationId).Distinct());
        Assert.All(results, result =>
        {
            Assert.Equal("INV-CONCURRENT-001", result.InvoiceReference);
            Assert.Equal(120, result.AmountSen);
            Assert.False(result.Paid);
        });

        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM obligations
            WHERE run_id = @run_id
            """,
            connection);

        command.Parameters.AddWithValue("run_id", runId);
        Assert.Equal(1L, (long)command.ExecuteScalar()!);

        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Single(ledger.GetJournals(runId));
    }

    [PostgresFact]
    public void Invoice_references_are_validated_case_sensitive_and_run_scoped()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FINMIND_TEST_DATABASE")!;

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(
            runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var invalidReferences = new string[]
        {
            null!,
            "",
            " ",
            " INV-001",
            "INV-001 ",
            "\tINV-001",
            "INV-001\n",
            "legacy:existing"
        };

        foreach (var reference in invalidReferences)
        {
            Assert.Throws<LedgerValidationException>(() =>
                ledger.CreateObligation(
                    runId, merchantId, 120, reference));
        }

        var first = ledger.CreateObligation(
            runId, merchantId, 120, "INV-SCOPED-001");

        var differentlyCased = ledger.CreateObligation(
            runId, merchantId, 120, "inv-scoped-001");

        Assert.NotEqual(first.ObligationId, differentlyCased.ObligationId);

        var otherRunId = Guid.NewGuid();
        var otherCashId = ledger.OpenRun(
            otherRunId, "open", 1_000, 200).CashAccountId;
        var otherMerchantId = ledger.CreateAccount(
            otherRunId, AccountKind.Merchant);

        var other = ledger.CreateObligation(
            otherRunId, otherMerchantId, 120, "INV-SCOPED-001");

        Assert.NotEqual(first.ObligationId, other.ObligationId);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.GetObligation(runId, other.ObligationId));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.GetObligation(otherRunId, first.ObligationId));

        using var connection = dataSource.OpenConnection();
        using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM obligations WHERE run_id = @run_id",
            connection);

        count.Parameters.AddWithValue("run_id", runId);
        Assert.Equal(2L, (long)count.ExecuteScalar()!);

        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(1_000, ledger.GetBalance(otherRunId, otherCashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Equal(0, ledger.GetBalance(otherRunId, otherMerchantId));
        Assert.Single(ledger.GetJournals(runId));
        Assert.Single(ledger.GetJournals(otherRunId));
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")))
            Skip = "Set FINMIND_TEST_DATABASE to run PostgreSQL integration tests.";
    }
}
