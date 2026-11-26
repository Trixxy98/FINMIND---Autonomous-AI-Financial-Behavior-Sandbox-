using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0019 step 2: OpenAI runs through the Responses API. Configuration, snapshot, and prices.
public sealed class OpenAIProviderTests
{
    private static JsonObject Config(string model = "gpt-6.2-sol", string? effort = "medium", Action<JsonObject>? change = null)
    {
        var settings = new JsonObject();
        if (effort is not null) settings["effort"] = effort;
        var config = new JsonObject
        {
            ["objective"] = "Manage the provided virtual funds responsibly.", ["scenarioId"] = "S01", ["seed"] = 1,
            ["startingBalanceSen"] = 100_000, ["transactionLimitSen"] = 50_000, ["durationDays"] = 3, ["maxActions"] = 50,
            ["maxModelTokens"] = 500_000, ["maxWallTimeSeconds"] = 600, ["memoryMode"] = "none", ["maxCostMicroUsd"] = 500_000,
            ["agent"] = new JsonObject { ["provider"] = "openai", ["modelId"] = model, ["settings"] = settings }
        };
        change?.Invoke(config);
        return config;
    }

    private static string[] Errors(JsonObject config) =>
        Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(config, null)).Fields.ToArray();

    [Fact]
    public void The_snapshot_records_the_adapter_the_token_bound_the_sampling_and_the_prices()
    {
        var snapshot = new InMemoryLedger().CreateManagedRun(Config(), null).ConfigSnapshot!;
        var agent = snapshot["agent"]!.AsObject();
        Assert.Equal(("openai", "gpt-6.2-sol", "medium"), ((string)agent["provider"]!, (string)agent["modelId"]!, (string)agent["settings"]!["effort"]!));
        Assert.Equal(("openai-responses/v1", "utf8-bytes+1024/v1", "provider-defaults"),
            ((string)agent["adapter"]!, (string)agent["tokenBound"]!, (string)agent["sampling"]!));
        Assert.Equal("openai-pricing/2026-11-26", (string)snapshot["pricingVersion"]!);
        Assert.Equal(PromptCatalog.CurrentVersion, (string)snapshot["promptVersion"]!);
    }

    [Fact]
    public void Claude_snapshots_are_unchanged_so_earlier_runs_stay_comparable()
    {
        var claude = Config(change: c => c["agent"] = new JsonObject
        {
            ["provider"] = "anthropic", ["modelId"] = "claude-opus-5-5", ["settings"] = new JsonObject { ["effort"] = "xhigh" }
        });
        var agent = new InMemoryLedger().CreateManagedRun(claude, null).ConfigSnapshot!["agent"]!.AsObject();
        Assert.Equal(["modelId", "provider", "settings"], agent.Select(p => p.Key).Order().ToArray());
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData(null)]
    public void Only_efforts_every_listed_model_accepts_are_allowed(string? effort)
    {
        Assert.Contains("agent.settings.effort", Errors(Config(effort: effort)));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Low_medium_and_high_are_allowed(string effort) =>
        Assert.NotNull(new InMemoryLedger().CreateManagedRun(Config(effort: effort), null));

    [Fact]
    public void A_cost_cap_is_required_and_unknown_models_are_refused()
    {
        Assert.Contains("maxCostMicroUsd", Errors(Config(change: c => c.Remove("maxCostMicroUsd"))));
        Assert.Contains("agent.modelId", Errors(Config(model: "gpt-unknown")));
    }

    [Fact]
    public void Prices_follow_the_published_table_with_cached_input_and_the_long_context_tier()
    {
        // gpt-6.2-sol: $2 input, $0.10 cached input, $10 output per million; above 272K input, $4 / $0.20 / $15.
        Assert.Equal(2_000_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 1_000_000, 0, 0, 0, requests: 4));
        Assert.Equal(100_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 0, 0, 1_000_000, 0, requests: 4));
        Assert.Equal(10_000_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 0, 1_000_000, 0, 0));
        Assert.Equal(1_200_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 300_000, 0, 0, 0));
        Assert.Equal(1_200_000 + 1_500_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 300_000, 100_000, 0, 0));
        // The worst case for a long prompt uses the long-context rates.
        Assert.Equal(1_200_000 + 150_000, PricingCatalog.WorstCaseMicroUsd("openai", "gpt-6.2-sol", 300_000, 10_000));
    }

    [Fact]
    public void Every_listed_model_has_a_price_and_the_catalog_is_not_verified_live()
    {
        var openai = ProviderCatalog.Paid["openai"];
        Assert.Equal(["gpt-6-astra", "gpt-6.1-sol", "gpt-6.2-astra", "gpt-6.2-sol"], openai.Prices.Keys.Order().ToArray());
        Assert.False(openai.VerifiedLive);
        Assert.Equal(["high", "low", "medium"], openai.Efforts.Order().ToArray());
    }
}
