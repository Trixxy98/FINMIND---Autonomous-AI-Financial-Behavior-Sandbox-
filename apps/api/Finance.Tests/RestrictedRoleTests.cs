// Menjalankan operasi ledger biasa sebagai finmind_api
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

public sealed class RestrictedRoleTests {
    [PostgresFact]
    public void Api_role_can_use_ledger_but_cannot_change_protected_data() {
        var connectionString = Environment.GetEnvironmentVariable(
            "FINMIND_API_TEST_DATABASE")
            ?? throw new InvalidOperationException(
                "FINMIND_API_TEST_DATABASE is required.");

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();

        var cashId = ledger.OpenRun(
            runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);
        var savingsId = ledger.CreateAccount(
            runId, AccountKind.AgentSavings);

        ledger.Transfer(
            runId, "transfer", cashId, merchantId, 100);
        
        ledger.MoveToSavings(
            runId, "savings", cashId, savingsId, 100);

        var obligation = ledger.CreateObligation(
            runId, merchantId, 100, "INV-RESTRICTED-001");

        ledger.PayObligation(
            runId, "payment", cashId, obligation.ObligationId);

        Assert.True(
            ledger.GetObligation(runId, obligation.ObligationId).Paid);
        Assert.Equal(700, ledger.GetBalance(runId, cashId));
        Assert.Equal(100, ledger.GetBalance(runId, savingsId));
        Assert.Equal(200, ledger.GetBalance(runId, merchantId));
        Assert.Equal(4, ledger.GetJournals(runId).Count);

        var forbiddenStatements = new[] {
            "UPDATE journal_entries SET description = 'changed'",
            "DELETE FROM journal_entries",
            "UPDATE journal_postings SET signed_amount_sen = 1",
            "DELETE FROM journal_postings",
            "TRUNCATE journal_entries",
            "UPDATE accounts SET is_active = FALSE",
            "UPDATE experiment_runs SET created_at = NOW()",
            "UPDATE obligations SET amount_sen = 1",
            "ALTER TABLE accounts ADD COLUMN forbidden BOOLEAN"
        };

        foreach (var sql in forbiddenStatements) {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand(sql, connection);

            var exception = Assert.Throws<PostgresException>(
                () => command.ExecuteNonQuery());

            Assert.Equal("42501", exception.SqlState);
        }

        Assert.Equal(700, ledger.GetBalance(runId, cashId));
        Assert.Equal(100, ledger.GetBalance(runId, savingsId));
        Assert.Equal(200, ledger.GetBalance(runId, merchantId));
        Assert.Equal(4, ledger.GetJournals(runId).Count);
    }

    // Open one run, membuat transfer, simpanan dan bayaran bil, then compare setiap baki akaun dgn jumlah postingnya
    [PostgresFact]
    public void Api_balance_reconcile_to_committed_postings() {
        var connectionString = Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!;

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();

        var cashId = ledger.OpenRun(runId, "open", 1_000, 300).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var savingsId = ledger.CreateAccount(runId, AccountKind.AgentSavings);

        ledger.Transfer(runId, "transfer", cashId, merchantId, 150);
        ledger.MoveToSavings(runId, "savings", cashId, savingsId, 200);

        var obligation = ledger.CreateObligation(
            runId, merchantId, 100, "INV-RECONCILE-001");
        ledger.PayObligation(
            runId, "payment", cashId, obligation.ObligationId);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(runId, "over-limit", cashId, merchantId, 301));

        var journals = ledger.GetJournals(runId);
        Assert.Equal(4, journals.Count);

        foreach (var journal in journals)
            Assert.Equal(0, journal.Postings.Sum(posting => posting.AmountSen));

        var totalsByAccount = journals
            .SelectMany(journal => journal.Postings)
            .GroupBy(posting => posting.AccountId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(posting => posting.AmountSen));

        foreach (var (accountId, postingTotal) in totalsByAccount)
            Assert.Equal(postingTotal, ledger.GetBalance(runId, accountId));

        Assert.Equal(550, ledger.GetBalance(runId, cashId));
        Assert.Equal(250, ledger.GetBalance(runId, merchantId));
        Assert.Equal(200, ledger.GetBalance(runId, savingsId));
    }
}