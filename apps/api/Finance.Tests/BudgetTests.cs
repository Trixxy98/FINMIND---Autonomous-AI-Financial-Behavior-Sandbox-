using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0011: model invocation records and server-enforced wall-time and token budgets.
[Collection(QueuedRunTests.Name)]
public sealed class BudgetTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Session(IFinanceLedger Ledger, ToolGateway Gateway, Guid RunId, string Token);

    private static Session Start(IFinanceLedger ledger, TimeProvider time, long maxTokens = 1_000, int wallSeconds = 600)
    {
        var gateway = new ToolGateway(ledger, time);
        var run = ledger.CreateManagedRun(new JsonObject
        {
            ["objective"] = "Manage funds.", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100_000,
            ["transactionLimitSen"] = 50_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = maxTokens,
            ["maxWallTimeSeconds"] = wallSeconds, ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        }, null);
        ledger.TransitionRun(run.RunId, RunStatus.Queued, null, "internal_api");
        string token;
        while (true)
        {
            var claim = gateway.Claim() ?? throw new InvalidOperationException("Run was not claimable.");
            if (claim.Run.RunId == run.RunId) { token = claim.Token; break; }
        }
        ledger.ActivateRun(run.RunId, FinanceEvents.WorkerSource);
        return new Session(ledger, gateway, run.RunId, token);
    }

    private static JsonObject Invocation(int turn, long input, long output) => new()
    {
        ["turn"] = turn, ["inputTokens"] = input, ["outputTokens"] = output, ["latencyMs"] = 12, ["outputText"] = "decided"
    };

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["token_budget_counts_once_per_turn_and_ends_the_run"] = ledger =>
        {
            var s = Start(ledger, TimeProvider.System, maxTokens: 100);
            var first = s.Gateway.RecordModelInvocation(s.RunId, Invocation(1, 40, 20));
            Assert.Equal(("ok", 60L), ((string)first["status"]!, (long)first["data"]!["tokens_used"]!));
            var retry = s.Gateway.RecordModelInvocation(s.RunId, Invocation(1, 40, 20));
            Assert.Equal((true, 60L), ((bool)retry["data"]!["replayed"]!, (long)retry["data"]!["tokens_used"]!));

            var over = s.Gateway.RecordModelInvocation(s.RunId, Invocation(2, 30, 11));
            Assert.Equal("TOKEN_BUDGET_EXCEEDED", (string?)over["reason_code"]);
            Assert.Equal(("BUDGET_EXCEEDED", AgentGatewayRules.TokenExhaustedReason),
                (ledger.GetRun(s.RunId).Status, ledger.GetRun(s.RunId).StatusReason));
            Assert.Null(s.Gateway.ResolveSession(s.Token));

            var events = ledger.GetEvents(s.RunId, 0, 500);
            Assert.Equal(["turn-0001", "turn-0002"], events.Where(e => e.EventType == "MODEL_INVOCATION_COMPLETED").Select(e => e.TraceId));
            Assert.Equal("turn-0002", events.Last().TraceId);
            Assert.Equal("gateway", events.Last().Source);
            Assert.Equal("RUN_NOT_ACTIVE", (string?)s.Gateway.RecordModelInvocation(s.RunId, Invocation(3, 0, 0))["reason_code"]);
        },
        ["invocation_shares_the_turn_trace_with_its_tool_call"] = ledger =>
        {
            var s = Start(ledger, TimeProvider.System);
            s.Gateway.RecordModelInvocation(s.RunId, Invocation(7, 0, 0));
            s.Gateway.Execute(s.RunId, "turn-0007", "get_balance", new JsonObject());
            var chain = ledger.GetEvents(s.RunId, 0, 500).Where(e => e.TraceId == "turn-0007").Select(e => e.EventType);
            Assert.Equal(["MODEL_INVOCATION_COMPLETED", "TOOL_CALL_PROPOSED", "TOOL_RETURNED"], chain);
        },
        ["invalid_invocations_are_rejected_without_records"] = ledger =>
        {
            var s = Start(ledger, TimeProvider.System);
            var before = ledger.GetEvents(s.RunId, 0, 500).Count;
            var invalid = s.Gateway.RecordModelInvocation(s.RunId, new JsonObject { ["turn"] = 0, ["inputTokens"] = -1, ["latencyMs"] = 1, ["extra"] = 1 });
            Assert.Equal("INVALID_TOOL_ARGUMENTS", (string?)invalid["reason_code"]);
            Assert.Equal(["extra", "inputTokens", "outputTokens", "turn"], invalid["data"]!["fields"]!.AsArray().Select(f => (string)f!));
            Assert.Equal(before, ledger.GetEvents(s.RunId, 0, 500).Count);
        },
        ["tool_only_turns_have_empty_text"] = ledger =>
        {
            var s = Start(ledger, TimeProvider.System);
            var response = s.Gateway.RecordModelInvocation(s.RunId, new JsonObject
            {
                ["turn"] = 1, ["inputTokens"] = 1, ["outputTokens"] = 1, ["latencyMs"] = 1, ["outputText"] = ""
            });
            Assert.Equal("ok", (string)response["status"]!);
        },
        ["invocation_text_is_redacted"] = ledger =>
        {
            var s = Start(ledger, TimeProvider.System);
            s.Gateway.RecordModelInvocation(s.RunId, new JsonObject
            {
                ["turn"] = 1, ["inputTokens"] = 0, ["outputTokens"] = 0, ["latencyMs"] = 0, ["outputText"] = "use sk-ant-abcdefghijklmnop " + new string('y', 3_000)
            });
            var text = (string)ledger.GetEvents(s.RunId, 0, 500).Single(e => e.EventType == "MODEL_INVOCATION_COMPLETED").Payload["outputText"]!;
            Assert.StartsWith("use [REDACTED] yyy", text);
            Assert.EndsWith(ToolArgumentRedaction.TruncatedMarker, text);
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
        using var dataSource = OwnerDataSource();
        Scenarios[scenario](new PostgresLedger(dataSource));
    }

    [Fact]
    public void InMemory_wall_time_budget_ends_the_run()
    {
        var time = new FakeTime();
        var ledger = new InMemoryLedger(time);
        var s = Start(ledger, time, wallSeconds: 60);
        Assert.Equal("ok", (string)s.Gateway.Execute(s.RunId, "turn-0001", "get_balance", new JsonObject())["status"]!);

        time.Now += TimeSpan.FromSeconds(60);
        var late = s.Gateway.Execute(s.RunId, "turn-0002", "get_balance", new JsonObject());
        Assert.Equal("WALL_TIME_BUDGET_EXCEEDED", (string?)late["reason_code"]);
        Assert.Equal(("BUDGET_EXCEEDED", AgentGatewayRules.WallTimeExhaustedReason),
            (ledger.GetRun(s.RunId).Status, ledger.GetRun(s.RunId).StatusReason));
        Assert.Null(s.Gateway.ResolveSession(s.Token));
    }

    [PostgresFact]
    public void Postgres_wall_time_uses_database_time()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var s = Start(ledger, TimeProvider.System, wallSeconds: 1);
        Thread.Sleep(1_200);

        var late = s.Gateway.RecordModelInvocation(s.RunId, Invocation(1, 0, 0));
        Assert.Equal("WALL_TIME_BUDGET_EXCEEDED", (string?)late["reason_code"]);
        Assert.Equal("BUDGET_EXCEEDED", ledger.GetRun(s.RunId).Status);
        Assert.Equal("RUN_NOT_ACTIVE", (string?)s.Gateway.Execute(s.RunId, "turn-0002", "get_balance", new JsonObject())["reason_code"]);
    }

    [PostgresFact]
    public void Postgres_token_usage_cannot_decrease()
    {
        using var dataSource = OwnerDataSource();
        var s = Start(new PostgresLedger(dataSource), TimeProvider.System);
        s.Gateway.RecordModelInvocation(s.RunId, Invocation(1, 5, 5));

        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("UPDATE experiment_runs SET model_tokens_used = 0 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", s.RunId);
        Assert.Equal("55000", Assert.Throws<PostgresException>(() => command.ExecuteNonQuery()).SqlState);
    }

    private static NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
}
