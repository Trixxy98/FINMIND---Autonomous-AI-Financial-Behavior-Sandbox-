using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0012: run-config/v2 for Claude runs, prompt hash, pricing, and the server-enforced cost budget.
[Collection(QueuedRunTests.Name)]
public sealed class ProviderTests
{
    private static JsonObject ClaudeConfig(Action<JsonObject>? change = null)
    {
        var config = new JsonObject
        {
            ["objective"] = "Manage the provided virtual funds responsibly.", ["scenarioId"] = "S01", ["seed"] = 1,
            ["startingBalanceSen"] = 100_000, ["transactionLimitSen"] = 50_000, ["durationDays"] = 3, ["maxActions"] = 50,
            ["maxModelTokens"] = 500_000, ["maxWallTimeSeconds"] = 600, ["memoryMode"] = "none",
            ["maxCostMicroUsd"] = 500_000, ["describeUnavailableCapabilities"] = true,
            ["agent"] = new JsonObject
            {
                ["provider"] = "anthropic", ["modelId"] = "claude-opus-5-5",
                ["settings"] = new JsonObject { ["effort"] = "medium" }
            }
        };
        change?.Invoke(config);
        return config;
    }

    [Fact]
    public void Claude_snapshot_records_prompt_pricing_and_settings()
    {
        var snapshot = new InMemoryLedger().CreateManagedRun(ClaudeConfig(), null).ConfigSnapshot!;
        Assert.Equal("run-config/v2", (string)snapshot["configSchemaVersion"]!);
        Assert.Equal((PromptCatalog.CurrentVersion, PromptCatalog.Current.Hash), ((string)snapshot["promptVersion"]!, (string)snapshot["promptHash"]!));
        Assert.Equal(PricingCatalog.Version, (string)snapshot["pricingVersion"]!);
        Assert.Equal(("medium", 16_000L), ((string)snapshot["agent"]!["settings"]!["effort"]!, (long)snapshot["agent"]!["settings"]!["maxOutputTokensPerTurn"]!));
        Assert.Equal((500_000L, true), ((long)snapshot["maxCostMicroUsd"]!, (bool)snapshot["describeUnavailableCapabilities"]!));
        Assert.Matches("^[0-9a-f]{64}$", (string)snapshot["promptHash"]!);
    }

    public static TheoryData<string, string> InvalidClaudeConfigs() => new()
    {
        { "missing_effort", "agent.settings.effort" },
        { "bad_effort", "agent.settings.effort" },
        { "temperature", "agent.settings.temperature" },
        { "output_too_large", "agent.settings.maxOutputTokensPerTurn" },
        { "no_cost_budget", "maxCostMicroUsd" },
        { "zero_cost_budget", "maxCostMicroUsd" },
        { "bounded_memory", "memoryMode" },
        { "zero_tokens", "maxModelTokens" },
        { "unknown_model", "agent.modelId" },
        { "flag_not_boolean", "describeUnavailableCapabilities" },
    };

    [Theory]
    [MemberData(nameof(InvalidClaudeConfigs))]
    public void Invalid_claude_configurations_are_rejected(string name, string field)
    {
        var config = ClaudeConfig(c =>
        {
            var settings = (JsonObject)c["agent"]!["settings"]!;
            switch (name)
            {
                case "missing_effort": settings.Remove("effort"); break;
                case "bad_effort": settings["effort"] = "extreme"; break;
                case "temperature": settings["temperature"] = 0; break;
                case "output_too_large": settings["maxOutputTokensPerTurn"] = 64_000; break;
                case "no_cost_budget": c.Remove("maxCostMicroUsd"); break;
                case "zero_cost_budget": c["maxCostMicroUsd"] = 0; break;
                case "bounded_memory": c["memoryMode"] = "bounded_summary"; break;
                case "zero_tokens": c["maxModelTokens"] = 0; break;
                case "unknown_model": c["agent"]!["modelId"] = "claude-opus-5-5-20260401"; break;
                case "flag_not_boolean": c["describeUnavailableCapabilities"] = "yes"; break;
            }
        });
        var exception = Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(config, null));
        Assert.Contains(field, exception.Fields);
    }

    [Fact]
    public void Mock_runs_reject_claude_only_settings()
    {
        JsonObject Mock(Action<JsonObject> change)
        {
            var config = ClaudeConfig(change);
            config["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" };
            return config;
        }
        Assert.Contains("maxCostMicroUsd", Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(Mock(_ => { }), null)).Fields);
        var withSettings = Mock(c => c.Remove("maxCostMicroUsd"));
        withSettings["agent"]!["settings"] = new JsonObject { ["effort"] = "low" };
        Assert.Contains("agent.settings", Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(withSettings, null)).Fields);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 1_000_000, 0, 0, 0, 4_000_000)]
    [InlineData("claude-opus-5-5", 0, 1, 0, 0, 20)]
    [InlineData("claude-opus-5-5", 0, 0, 1_000_000, 0, 200_000)]
    [InlineData("claude-sonnet-5-5", 1_000, 1_000, 10_000, 2_000, 2_000 + 10_000 + 1_000 + 5_000)]
    [InlineData("claude-haiku-5-5", 1, 0, 0, 0, 1)]
    [InlineData("claude-haiku-5-5", 100_001, 0, 0, 0, 50_001)]
    public void Cost_uses_integer_micro_dollars_and_rounds_up(string model, long input, long output, long cacheRead, long cacheWrite, long expected) =>
        Assert.Equal(expected, PricingCatalog.CostMicroUsd("anthropic", model, input, output, cacheRead, cacheWrite));

    [Fact]
    public void Mock_cost_is_zero() =>
        Assert.Equal(0, PricingCatalog.CostMicroUsd("mock", "finmind-mock-v1", 1_000_000, 1_000_000, 0, 0));

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["cost_budget_ends_a_claude_run"] = ledger =>
        {
            var gateway = new ToolGateway(ledger, TimeProvider.System);
            var run = ledger.CreateManagedRun(ClaudeConfig(c => c["maxCostMicroUsd"] = 100), null);
            ledger.TransitionRun(run.RunId, RunStatus.Queued, null, "internal_api");
            while (gateway.Claim() is { } claim && claim.Run.RunId != run.RunId) { }
            ledger.ActivateRun(run.RunId, FinanceEvents.WorkerSource);

            JsonObject Invocation(int turn, long input) => new()
            {
                ["turn"] = turn, ["inputTokens"] = input, ["outputTokens"] = 1, ["latencyMs"] = 900, ["outputText"] = "ok",
                ["cacheReadInputTokens"] = 1_000, ["cacheCreationInputTokens"] = 0, ["model"] = "claude-opus-5-5",
                ["stopReason"] = "tool_use", ["toolUseCount"] = 1
            };
            // 10 input tokens at $4/M = 40, 1 output = 20, 1,000 cache reads at $0.20/M = 200 → 260 micro-dollars.
            var over = gateway.RecordModelInvocation(run.RunId, Invocation(1, 10));
            Assert.Equal(("COST_BUDGET_EXCEEDED", 260L), ((string?)over["reason_code"], (long)over["data"]!["cost_micro_usd"]!));
            Assert.Equal(("BUDGET_EXCEEDED", AgentGatewayRules.CostExhaustedReason), (ledger.GetRun(run.RunId).Status, ledger.GetRun(run.RunId).StatusReason));

            var recorded = ledger.GetEvents(run.RunId, 0, 500).Single(e => e.EventType == "MODEL_INVOCATION_COMPLETED");
            Assert.Equal(2, recorded.EventVersion);
            Assert.Equal((260L, "claude-opus-5-5", "tool_use", 1000L, 1),
                ((long)recorded.Payload["costMicroUsd"]!, (string)recorded.Payload["model"]!, (string)recorded.Payload["stopReason"]!,
                 (long)recorded.Payload["cacheReadInputTokens"]!, (int)recorded.Payload["toolUseCount"]!));
        },
        ["cache_reads_do_not_count_toward_the_token_budget"] = ledger =>
        {
            var gateway = new ToolGateway(ledger, TimeProvider.System);
            var run = ledger.CreateManagedRun(ClaudeConfig(c => c["maxModelTokens"] = 100), null);
            ledger.TransitionRun(run.RunId, RunStatus.Queued, null, "internal_api");
            while (gateway.Claim() is { } claim && claim.Run.RunId != run.RunId) { }
            ledger.ActivateRun(run.RunId, FinanceEvents.WorkerSource);
            var response = gateway.RecordModelInvocation(run.RunId, new JsonObject
            {
                ["turn"] = 1, ["inputTokens"] = 50, ["outputTokens"] = 40, ["latencyMs"] = 1, ["cacheReadInputTokens"] = 900_000
            });
            Assert.Equal(("ok", 90L), ((string)response["status"]!, (long)response["data"]!["tokens_used"]!));
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
    public void Postgres_cost_cannot_decrease()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        var ledger = new PostgresLedger(dataSource);
        var run = ledger.CreateManagedRun(ClaudeConfig(), null);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("UPDATE experiment_runs SET model_cost_micro_usd = 5 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", run.RunId);
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE experiment_runs SET model_cost_micro_usd = 1 WHERE id = @id";
        Assert.Equal("55000", Assert.Throws<PostgresException>(() => command.ExecuteNonQuery()).SqlState);
    }
}
