using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0019 step 3: Groq runs through its OpenAI-compatible Chat Completions. Configuration, snapshot, and prices.
public sealed class GroqProviderTests
{
    private static JsonObject Config(string model = "openai/gpt-oss-120b", string? effort = "medium")
    {
        var settings = new JsonObject();
        if (effort is not null) settings["effort"] = effort;
        return new JsonObject
        {
            ["objective"] = "Manage the provided virtual funds responsibly.", ["scenarioId"] = "S01", ["seed"] = 1,
            ["startingBalanceSen"] = 100_000, ["transactionLimitSen"] = 50_000, ["durationDays"] = 3, ["maxActions"] = 50,
            ["maxModelTokens"] = 500_000, ["maxWallTimeSeconds"] = 600, ["memoryMode"] = "none", ["maxCostMicroUsd"] = 500_000,
            ["agent"] = new JsonObject { ["provider"] = "groq", ["modelId"] = model, ["settings"] = settings }
        };
    }

    [Fact]
    public void The_snapshot_records_the_groq_adapter_and_prices()
    {
        var snapshot = new InMemoryLedger().CreateManagedRun(Config(), null).ConfigSnapshot!;
        var agent = snapshot["agent"]!.AsObject();
        Assert.Equal(("groq-chat/v1", "utf8-bytes+1024/v1", "provider-defaults"),
            ((string)agent["adapter"]!, (string)agent["tokenBound"]!, (string)agent["sampling"]!));
        Assert.Equal("groq-pricing/2026-11-26", (string)snapshot["pricingVersion"]!);
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("none")]
    [InlineData("default")]
    [InlineData(null)]
    public void Only_the_documented_gpt_oss_efforts_are_allowed(string? effort) =>
        Assert.Contains("agent.settings.effort",
            Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(Config(effort: effort), null)).Fields);

    [Fact]
    public void Only_production_models_with_published_prices_are_listed()
    {
        var groq = ProviderCatalog.Paid["groq"];
        Assert.Equal(["openai/gpt-oss-120b", "openai/gpt-oss-20b"], groq.Prices.Keys.Order().ToArray());
        Assert.False(groq.VerifiedLive);
        Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(Config(model: "llama-3.3-70b-versatile"), null));
    }

    [Fact]
    public void Prices_follow_the_models_page()
    {
        // gpt-oss-120b: $0.15 input, $0.60 output per million; gpt-oss-20b: $0.075 / $0.30. No cached-input price is listed.
        Assert.Equal(150_000 + 600_000, PricingCatalog.CostMicroUsd("groq", "openai/gpt-oss-120b", 1_000_000, 1_000_000, 0, 0, requests: 10));
        Assert.Equal(75_000 + 300_000, PricingCatalog.CostMicroUsd("groq", "openai/gpt-oss-20b", 1_000_000, 1_000_000, 0, 0, requests: 10));
        Assert.Equal(150_000, PricingCatalog.CostMicroUsd("groq", "openai/gpt-oss-120b", 0, 0, 1_000_000, 0, requests: 10));
    }
}
