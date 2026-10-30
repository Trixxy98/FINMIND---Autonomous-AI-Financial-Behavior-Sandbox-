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

        var first = ledger.OpenRun(runId, "open-1", 25_000);
        var restartedLedger = new PostgresLedger(dataSource);
        var retry = restartedLedger.OpenRun(runId, "open-1", 25_000);

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
        var cashId = ledger.OpenRun(runId, "open", 100).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var first = ledger.Transfer(runId, "first-transfer", cashId, merchantId, 10, 100);
        var replay = ledger.Transfer(runId, "first-transfer", cashId, merchantId, 10, 100);

        Assert.Equal(first.JournalId, replay.JournalId);
        Assert.True(replay.WasIdempotentReplay);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
        {
            try
            {
                ledger.Transfer(runId, $"concurrent-{index}", cashId, merchantId, 10, 100);
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
        var cashId = ledger.OpenRun(runId, "open", 500).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var obligation = ledger.CreateObligation(runId, merchantId, 120);

        var payment = ledger.PayObligation(runId, "bill-1", cashId, obligation.ObligationId, 200);
        var retry = ledger.PayObligation(runId, "bill-1", cashId, obligation.ObligationId, 200);

        Assert.Equal(payment.JournalId, retry.JournalId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.True(new PostgresLedger(dataSource).GetObligation(runId, obligation.ObligationId).Paid);
        Assert.Equal(380, ledger.GetBalance(runId, cashId));
        Assert.Equal(120, ledger.GetBalance(runId, merchantId));
        Assert.Throws<LedgerValidationException>(() => ledger.PayObligation(runId, "bill-2", cashId, obligation.ObligationId, 200));
    }

    [PostgresFact]
    public async Task Concurrent_opening_funding_is_single_and_cross_run_spending_is_denied()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!;
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => ledger.OpenRun(runId, "shared-open", 1_000))));

        Assert.Single(results.Select(result => result.CashAccountId).Distinct());
        Assert.Single(ledger.GetJournals(runId));

        var otherRunId = Guid.NewGuid();
        var otherCash = ledger.OpenRun(otherRunId, "other-open", 1_000).CashAccountId;
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(runId, "cross-run", otherCash, merchant, 10, 100));
        Assert.Equal(1_000, ledger.GetBalance(otherRunId, otherCash));
        Assert.Equal(0, ledger.GetBalance(runId, merchant));
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
