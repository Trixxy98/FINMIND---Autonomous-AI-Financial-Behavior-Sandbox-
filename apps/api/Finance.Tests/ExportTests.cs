using System.Security.Cryptography;
using System.Text;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0015 step 4: export formats.
[Collection(QueuedRunTests.Name)]
public sealed class ExportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]   // spreadsheet formula injection
    [InlineData("+1", "'+1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-x", "'-x")]
    [InlineData("", "")]
    public void Csv_text_cells_are_quoted_and_never_formulas(string value, string expected) =>
        Assert.Equal(expected, RunExport.CsvText(value));

    [Fact]
    public void Csv_numbers_are_written_as_numbers() => Assert.Equal("-150000", RunExport.CsvNumber(-150_000));

    // The exported hash input must be exactly what the chain hashes, for every scheme in use.
    [PostgresFact]
    public void Exported_hash_inputs_reproduce_the_stored_hashes()
    {
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        using var api = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var ledger = new PostgresLedger(api);
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 10_000, 5_000).CashAccountId;
        ledger.Transfer(run, "t", cash, ledger.CreateAccount(run, AccountKind.Merchant), 100);

        var lines = new ResearchReads(api).Export(run)!;
        var events = lines.Where(l => (string)l["type"]! == "event").ToList();
        Assert.NotEmpty(events);
        foreach (var evt in events)
        {
            var input = (string)evt["hashInput"]!;
            Assert.Equal((string)evt["eventHash"]!, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))));
            Assert.StartsWith((int)evt["hashVersion"]! == 2 ? "finmind-event-v2\n" : "finmind-event-v1\n", input);
        }
    }

    // New events are v2, so a v1 event (written before migration 014) is built here, inside a rolled-back transaction.
    [PostgresFact]
    public void Exported_hash_input_reproduces_a_version_1_hash()
    {
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        var run = Guid.NewGuid();
        new PostgresLedger(owner).OpenRun(run, "open", 10_000, 5_000);
        using var connection = owner.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand(
            "SET LOCAL session_replication_role = replica; " +
            "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, payload, prev_hash, event_hash, hash_version) " +
            "SELECT gen_random_uuid(), @run, 1000, 'FINANCE_ERROR', 1, now(), 'internal_api', '{\"note\":\"é ✓\"}', '\\x01', " +
            "finmind_run_event_hash('\\x01', @run, 1000, 'FINANCE_ERROR', 1, now(), 'internal_api', '{\"note\":\"é ✓\"}'::jsonb), 1; " +
            $"SELECT bool_and(sha256(convert_to({RunExport.HashInputSql}, 'UTF8')) = event_hash) FROM run_events WHERE run_id = @run AND hash_version = 1",
            connection, transaction);
        command.Parameters.AddWithValue("run", run);
        Assert.Equal(true, command.ExecuteScalar());
        transaction.Rollback();
    }
}
