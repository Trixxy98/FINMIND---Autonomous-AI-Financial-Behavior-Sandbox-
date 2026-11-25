using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// The stop is database-wide, so these tests run alone: no other test's run is cancelled or refused.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OperatorStopCollection
{
    public const string Name = "Operator stop (runs alone)";
}

// ADR-0017 step 2: one switch stops every active run and refuses new starts and claims until resumed.
[Collection(OperatorStopCollection.Name)]
public sealed class OperatorStopTests
{
    private static Guid NewRun(IFinanceLedger ledger) => ledger.CreateManagedRun(new JsonObject
    {
        ["objective"] = "Manage funds.", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100_000,
        ["transactionLimitSen"] = 50_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = 0,
        ["maxWallTimeSeconds"] = 3_600, ["memoryMode"] = "none",
        ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
    }, null).RunId;

    private static Guid Running(IFinanceLedger ledger, ToolGateway gateway)
    {
        var run = NewRun(ledger);
        ledger.TransitionRun(run, RunStatus.Queued, null, FinanceEvents.InternalApiSource);
        while (gateway.Claim() is { } claim)
            if (claim.Run.RunId == run)
            {
                ledger.ActivateRun(run, FinanceEvents.WorkerSource);
                return run;
            }
        throw new InvalidOperationException("Run was not claimable.");
    }

    private static LedgerReasonCode Refusal(Action action) => Assert.ThrowsAny<LedgerException>(action).ReasonCode;

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["stop_cancels_active_runs_and_refuses_starts_and_claims"] = ledger =>
        {
            var gateway = new ToolGateway(ledger, TimeProvider.System);
            ledger.ResumeAll("test start", FinanceEvents.InternalApiSource);
            var running = Running(ledger, gateway);
            var queued = NewRun(ledger);
            ledger.TransitionRun(queued, RunStatus.Queued, null, FinanceEvents.InternalApiSource);
            var draft = NewRun(ledger);

            var result = ledger.StopAll("  drill  ", FinanceEvents.InternalApiSource);

            Assert.True(result.State.Stopped);
            Assert.Equal("drill", result.State.Reason);
            Assert.Contains(running, result.CancelledRunIds);
            Assert.Contains(queued, result.CancelledRunIds);
            foreach (var run in new[] { running, queued })
                Assert.Equal(("CANCELLED", "Operator stop: drill"), (ledger.GetRun(run).Status, ledger.GetRun(run).StatusReason));
            Assert.Equal("DRAFT", ledger.GetRun(draft).Status);
            Assert.Equal(LedgerReasonCode.OperatorStopped,
                Refusal(() => ledger.TransitionRun(draft, RunStatus.Queued, null, FinanceEvents.InternalApiSource)));
            Assert.Equal("DRAFT", ledger.GetRun(draft).Status);
            Assert.Null(gateway.Claim());
            ledger.ResumeAll("test end", FinanceEvents.InternalApiSource);
        },
        ["resume_reopens_and_both_are_recorded_in_order"] = ledger =>
        {
            ledger.ResumeAll("test start", FinanceEvents.InternalApiSource);
            var draft = NewRun(ledger);
            ledger.StopAll("first", FinanceEvents.InternalApiSource);
            var resumed = ledger.ResumeAll("checked", FinanceEvents.InternalApiSource);
            Assert.False(resumed.Stopped);
            Assert.False(ledger.GetOperatorState().Stopped);
            Assert.Equal("QUEUED", ledger.TransitionRun(draft, RunStatus.Queued, null, FinanceEvents.InternalApiSource).Status);
            var history = ledger.OperatorHistory(3);
            Assert.Equal([("resume", "checked"), ("stop", "first"), ("resume", "test start")],
                history.Select(h => (h.Action, h.Reason)).ToArray());
            ledger.TransitionRun(draft, RunStatus.Cancelled, "test cleanup", FinanceEvents.InternalApiSource);
        },
        ["a_reason_is_required"] = ledger =>
        {
            Assert.Equal(LedgerReasonCode.MissingRequiredField, Refusal(() => ledger.StopAll("   ", FinanceEvents.InternalApiSource)));
            Assert.Equal(LedgerReasonCode.MissingRequiredField, Refusal(() => ledger.ResumeAll(null, FinanceEvents.InternalApiSource)));
            Assert.Equal(500, ledger.ResumeAll(new string('r', 900), FinanceEvents.InternalApiSource).Reason!.Length);
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
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        Scenarios[scenario](new PostgresLedger(dataSource));
    }

    // Starts racing a stop: afterwards no run is active, whichever side won each race.
    [PostgresFact]
    public void Postgres_starts_racing_a_stop_leave_no_active_run()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var ledger = new PostgresLedger(dataSource);
        ledger.ResumeAll("test start", FinanceEvents.InternalApiSource);
        var runs = Enumerable.Range(0, 24).Select(_ => NewRun(ledger)).ToArray();
        using var go = new ManualResetEventSlim();
        var starts = runs.Select(run => Task.Run(() =>
        {
            go.Wait();
            try { ledger.TransitionRun(run, RunStatus.Queued, null, FinanceEvents.InternalApiSource); }
            catch (LedgerException e) when (e.ReasonCode == LedgerReasonCode.OperatorStopped) { }
        })).ToArray();
        var stop = Task.Run(() => { go.Wait(); Thread.Sleep(2); return ledger.StopAll("race", FinanceEvents.InternalApiSource); });
        go.Set();
        Task.WaitAll([.. starts, stop]);
        var statuses = runs.Select(r => ledger.GetRun(r).Status).ToArray();
        Assert.All(statuses, s => Assert.Contains(s, new[] { "DRAFT", "CANCELLED" }));
        ledger.ResumeAll("test end", FinanceEvents.InternalApiSource);
        foreach (var run in runs.Where(r => ledger.GetRun(r).Status == "DRAFT"))
            ledger.TransitionRun(run, RunStatus.Cancelled, "test cleanup", FinanceEvents.InternalApiSource);
    }

    [PostgresFact]
    public void Postgres_api_role_cannot_change_or_delete_the_switch_history()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        new PostgresLedger(dataSource).ResumeAll("history row", FinanceEvents.InternalApiSource);
        using var connection = dataSource.OpenConnection();
        foreach (var sql in new[] { "UPDATE operator_switch_log SET reason = 'x'", "DELETE FROM operator_switch_log",
                                    "UPDATE export_log SET format = 'csv'", "DELETE FROM export_log" })
            Assert.ThrowsAny<PostgresException>(() => new NpgsqlCommand(sql, connection).ExecuteNonQuery());
    }
}
