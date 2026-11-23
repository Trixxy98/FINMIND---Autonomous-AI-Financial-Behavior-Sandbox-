using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0013 part B: the evidence label of an action.
public sealed class ResearchReadTests
{
    private static readonly Guid Journal = Guid.NewGuid();

    [Theory]
    [InlineData("move_to_savings", "committed", true, "committed")]
    [InlineData("move_to_savings", "replayed", true, "committed")]      // a resumed call that had committed
    [InlineData("move_to_savings", "denied", true, "committed")]        // item 16: the ledger, not the outcome
    [InlineData("move_to_savings", null, true, "committed")]            // interrupted after commit
    [InlineData("move_to_savings", "replayed", false, "proposed")]      // another call's key
    [InlineData("pay_bill", "denied", false, "denied")]
    [InlineData("transfer_funds", "malformed", false, "denied")]
    [InlineData("do_anything", "unsupported_capability", false, "denied")]
    [InlineData("pay_bill", "conflict", false, "denied")]
    [InlineData("pay_bill", "internal_error", false, "proposed")]
    [InlineData("pay_bill", null, false, "proposed")]
    [InlineData("submit_plan", "ok", false, "reported_by_agent")]
    [InlineData("finish", "ok", false, "reported_by_agent")]
    [InlineData("request_funding", "ok", false, "reported_by_agent")]
    [InlineData("submit_plan", "denied", false, "denied")]
    [InlineData("get_balance", "ok", false, "proposed")]
    [InlineData("wait", "ok", false, "proposed")]
    public void Labels_the_strongest_fact(string tool, string? outcome, bool committedByThisCall, string expected) =>
        Assert.Equal(expected, ActionEvidence.Of(tool, outcome, committedByThisCall ? Journal : null));

    // Runs created before the event log (ADR-0007) have journals but no LEDGER_COMMITTED events.
    [PostgresFact]
    public void Journals_from_before_the_event_log_are_listed_first_and_do_not_reconcile()
    {
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        using var api = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var ledger = new PostgresLedger(owner);
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 1_000, 500).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        var old = ledger.Transfer(run, "old", cash, merchant, 10).JournalId;
        using (var connection = owner.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            // Test database only: reproduces the pre-ADR-0007 shape of a legacy run.
            using var forget = new NpgsqlCommand("SET LOCAL session_replication_role = replica; DELETE FROM run_events WHERE run_id = @run",
                connection, transaction);
            forget.Parameters.AddWithValue("run", run);
            forget.ExecuteNonQuery();
            transaction.Commit();
        }
        var recent = ledger.Transfer(run, "new", cash, merchant, 20).JournalId;

        var reads = new ResearchReads(api);
        var first = reads.Ledger(run, 0, 1)!;
        Assert.Equal([OpeningOf(reads, run), old, recent],
            first["journals"]!.AsArray().Select(j => j!["journalId"]!.GetValue<Guid>()));
        Assert.Empty(reads.Ledger(run, (long)first["nextAfterSequence"]!, 100)!["journals"]!.AsArray());
        Assert.False((bool)reads.Wallets(run)!["reconciled"]!);
        Assert.False((bool)reads.GetRun(run)!["researchEligible"]!);
    }

    private static Guid OpeningOf(ResearchReads reads, Guid run) =>
        reads.Ledger(run, 0, 500)!["journals"]!.AsArray().First(j => (string)j!["kind"]! == "opening_funding")!["journalId"]!.GetValue<Guid>();
}
