using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

public sealed class PostgresIntegrityTests {
    [PostgresFact]
    public void Invalid_journals_cannot_commit_or_leave_partial_history() {
        using var dataSource = CreateDataSource();
        var ledger = new PostgresLedger(dataSource);

        // Zer0 postings, one posting, and two unbalanced postings.
        foreach (var postingCount in new[] {0, 1, 2}) {
            var runId = Guid.NewGuid();
            var cashId = ledger.OpenRun(runId, "open", 1_000, 200)
                .CashAccountId;
            var merchantId = ledger.CreateAccount(
                runId, AccountKind.Merchant);
            var journalId = Guid.NewGuid();

            using (var connection = dataSource.OpenConnection())
            using (var transaction = connection.BeginTransaction()) {
                using var journal = new NpgsqlCommand(
                    """
                    INSERT INTO journal_entries
                        (id, run_id, kind, idempotency_key, description)
                    VALUES
                        (@id, @run_id, 'transfer', 'invalid',
                        'database integrity test')
                    """,
                    connection,transaction);

                journal.Parameters.AddWithValue("id", journalId);
                journal.Parameters.AddWithValue("run_id", runId);
                journal.ExecuteNonQuery();

                if (postingCount >= 1) {
                    InsertPosting(
                        connection, transaction,
                        journalId, runId, cashId, -100
                    );
                }

                if (postingCount == 2) {
                    InsertPosting(
                        connection, transaction,
                        journalId, runId, merchantId, 99
                    );
                }

                // Balance checks are deferred until transaction commit.
                var exception = Assert.Throws<PostgresException>(
                    () => transaction.Commit());

                Assert.Equal("23514", exception.SqlState);
            }

            Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
            Assert.Equal(0, ledger.GetBalance(runId, merchantId));
            Assert.Single(ledger.GetJournals(runId));

            // Check the tables directly, including orphan headers
            // that a journal/posting join could otherwise conceal.

            using var verification = dataSource.OpenConnection();
            using var count = new NpgsqlCommand(
                """
                SELECT
                    (SELECT COUNT(*) FROM journal_entries
                    WHERE id = @id)
                    +
                    (SELECT COUNT(*) FROM journal_postings
                    WHERE entry_id = @id)
                """,
                verification);

            count.Parameters.AddWithValue("id", journalId);
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
    }

    [PostgresFact]
    public void Committed_journals_and_postings_cannot_be_changed_or_deleted() {
        using var dataSource = CreateDataSource();
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(runId, "open", 1_000, 200)
            .CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var payment = ledger.Transfer(
            runId, "transfer", cashId, merchantId, 100);

        var original = ledger.GetJournals(runId)
            .Single(journal => journal.JournalId == payment.JournalId);

        var mutations = new[]
        {
            """
            UPDATE journal_entries
            SET description = 'changed'
            WHERE id = @id
            """,
            """
            DELETE FROM journal_entries
            WHERE id = @id
            """,
            """
            UPDATE journal_postings
            SET signed_amount_sen = signed_amount_sen + 1
            WHERE entry_id = @id
            """,
            """
            DELETE FROM journal_postings
            WHERE entry_id = @id
            """
        };

        foreach (var sql in mutations)
        {
            using (var connection = dataSource.OpenConnection())
            using (var command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue(
                    "id", payment.JournalId);

                var exception = Assert.Throws<PostgresException>(
                    () => command.ExecuteNonQuery());

                Assert.Equal("55000", exception.SqlState);
            }

            Assert.Equal(900, ledger.GetBalance(runId, cashId));
            Assert.Equal(100, ledger.GetBalance(runId, merchantId));

            var journals = ledger.GetJournals(runId);
            Assert.Equal(2, journals.Count);

            var unchanged = journals.Single(
                journal => journal.JournalId == payment.JournalId);

            Assert.Equal(original.Description, unchanged.Description);
            Assert.Equal(original.Kind, unchanged.Kind);
            Assert.Equal(
                original.IdempotencyKey, unchanged.IdempotencyKey);
            Assert.Equal(
                original.Postings.ToArray(),
                unchanged.Postings.ToArray());
        }
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "FINMIND_TEST_DATABASE")
            ?? throw new InvalidOperationException(
                "FINMIND_TEST_DATABASE is required.");

        return NpgsqlDataSource.Create(connectionString);
    }

    private static void InsertPosting(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        Guid runId,
        Guid accountId,
        long amountSen)
    {
        using var command = new NpgsqlCommand(
            """
            INSERT INTO journal_postings
                (entry_id, run_id, account_id,
                 currency, signed_amount_sen)
            VALUES
                (@entry_id, @run_id, @account_id, 'MYR', @amount)
            """,
            connection, transaction);

        command.Parameters.AddWithValue("entry_id", journalId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("amount", amountSen);
        command.ExecuteNonQuery();

    }
}