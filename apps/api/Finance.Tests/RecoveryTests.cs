using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0013 A1 (items 3 and 5): a lost response or a lost worker never strands or kills a run.
[Collection(QueuedRunTests.Name)]
public sealed class RecoveryTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Claimed(FakeTime Time, ToolGateway Gateway, Guid RunId, string Token);

    private static Claimed Claim(IFinanceLedger ledger)
    {
        var time = new FakeTime();
        var gateway = new ToolGateway(ledger, time);
        var run = ledger.CreateManagedRun(new JsonObject
        {
            ["objective"] = "Manage funds.", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100_000,
            ["transactionLimitSen"] = 50_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = 0,
            ["maxWallTimeSeconds"] = 3_600, ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        }, null).RunId;
        ledger.TransitionRun(run, RunStatus.Queued, null, "internal_api");
        while (gateway.Claim() is { } claim)
            if (claim.Run.RunId == run) return new Claimed(time, gateway, run, claim.Token);
        throw new InvalidOperationException("Run was not claimable.");
    }

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["retried_initialize_is_idempotent"] = ledger =>
        {
            var c = Claim(ledger);
            var first = ledger.ActivateRun(c.RunId, "worker");
            var events = ledger.GetEvents(c.RunId, 0, 500).Count;
            var retry = ledger.ActivateRun(c.RunId, "worker");
            Assert.Equal((first.JournalId, first.CashAccountId, true), (retry.JournalId, retry.CashAccountId, retry.WasIdempotentReplay));
            Assert.Equal(events, ledger.GetEvents(c.RunId, 0, 500).Count);
            Assert.Equal("RUNNING", ledger.GetRun(c.RunId).Status);
        },
        ["lost_claim_is_reclaimed_after_expiry"] = ledger =>
        {
            var c = Claim(ledger);
            c.Time.Now += TimeSpan.FromMinutes(11);
            while (c.Gateway.Claim() is { } again)
            {
                if (again.Run.RunId != c.RunId) continue;
                Assert.Null(c.Gateway.ResolveSession(c.Token));
                Assert.Equal(c.RunId, c.Gateway.ResolveSession(again.Token));
                return;
            }
            Assert.Fail("The run was never offered again.");
        },
        ["running_run_with_expired_session_is_failed"] = ledger =>
        {
            var c = Claim(ledger);
            ledger.ActivateRun(c.RunId, "worker");
            c.Time.Now += TimeSpan.FromMinutes(11);
            c.Gateway.Claim();
            Assert.Equal(("FAILED", "Worker session expired."), (ledger.GetRun(c.RunId).Status, ledger.GetRun(c.RunId).StatusReason));
        },
        ["running_run_with_live_session_is_left_alone"] = ledger =>
        {
            var c = Claim(ledger);
            ledger.ActivateRun(c.RunId, "worker");
            c.Time.Now += TimeSpan.FromMinutes(9);
            c.Gateway.Claim();
            Assert.Equal("RUNNING", ledger.GetRun(c.RunId).Status);
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
        // The restricted role, so the grants are tested too.
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        Scenarios[scenario](new PostgresLedger(dataSource));
    }
}
