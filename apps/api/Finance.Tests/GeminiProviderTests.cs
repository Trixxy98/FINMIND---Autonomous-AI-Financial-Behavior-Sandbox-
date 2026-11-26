using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0019 step 4: Gemini through the Interactions API, and prices that change on a date.
public sealed class GeminiProviderTests
{
    private static JsonObject Config(string model = "gemini-4-hydrogen", string? effort = "medium")
    {
        var settings = new JsonObject();
        if (effort is not null) settings["effort"] = effort;
        return new JsonObject
        {
            ["objective"] = "Manage the provided virtual funds responsibly.", ["scenarioId"] = "S01", ["seed"] = 1,
            ["startingBalanceSen"] = 100_000, ["transactionLimitSen"] = 50_000, ["durationDays"] = 3, ["maxActions"] = 50,
            ["maxModelTokens"] = 500_000, ["maxWallTimeSeconds"] = 600, ["memoryMode"] = "none", ["maxCostMicroUsd"] = 500_000,
            ["agent"] = new JsonObject { ["provider"] = "google", ["modelId"] = model, ["settings"] = settings }
        };
    }

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc + "Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_snapshot_records_the_interactions_adapter_and_prices()
    {
        var snapshot = new InMemoryLedger().CreateManagedRun(Config(), null).ConfigSnapshot!;
        var agent = snapshot["agent"]!.AsObject();
        Assert.Equal(("gemini-interactions/v1", "utf8-bytes+1024/v1", "provider-defaults"),
            ((string)agent["adapter"]!, (string)agent["tokenBound"]!, (string)agent["sampling"]!));
        Assert.Equal("google-pricing/2026-11-26", (string)snapshot["pricingVersion"]!);
        Assert.Equal("Google (Gemini)", ProviderCatalog.Paid["google"].Label);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("xhigh")]
    [InlineData(null)]
    public void Only_levels_every_listed_model_accepts_are_allowed(string? effort) =>
        Assert.Contains("agent.settings.effort",
            Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(Config(effort: effort), null)).Fields);

    [Fact]
    public void The_listed_models_have_published_prices()
    {
        var google = ProviderCatalog.Paid["google"];
        Assert.Equal(["gemini-3.6-flash", "gemini-3.8-flash", "gemini-4-argon", "gemini-4-carbon", "gemini-4-hydrogen", "gemini-4-neon"],
            google.Prices.Keys.Order().ToArray());
        Assert.False(google.VerifiedLive);
        // gemini-4-hydrogen: $1.50 input, $7.50 output (thinking included), $0.075 cached input per million.
        Assert.Equal(1_500_000 + 7_500_000 + 75_000,
            PricingCatalog.CostMicroUsd("google", "gemini-4-hydrogen", 1_000_000, 1_000_000, 1_000_000, 0, requests: 10));
    }

    [Fact]
    public void A_price_applies_from_its_date_and_the_earlier_price_until_then()
    {
        // Carbon and Argon double on 1 December 2026; 3.8 Flash and 3.6 Flash on 1 January 2027 (UTC midnight used).
        Assert.Equal(2_000_000, PricingCatalog.CostMicroUsd("google", "gemini-4-carbon", 1_000_000, 0, 0, 0, at: At("2026-11-30T23:59:59")));
        Assert.Equal(4_000_000, PricingCatalog.CostMicroUsd("google", "gemini-4-carbon", 1_000_000, 0, 0, 0, at: At("2026-12-01T00:00:00")));
        Assert.Equal(20_000_000, PricingCatalog.CostMicroUsd("google", "gemini-4-argon", 0, 1_000_000, 0, 0, at: At("2026-12-15T00:00:00")));
        Assert.Equal(750_000, PricingCatalog.CostMicroUsd("google", "gemini-3.8-flash", 1_000_000, 0, 0, 0, at: At("2026-12-31T23:59:59")));
        Assert.Equal(1_500_000, PricingCatalog.CostMicroUsd("google", "gemini-3.6-flash", 1_000_000, 0, 0, 0, at: At("2027-01-01T00:00:00")));
        Assert.Equal(4_000_000 + 200_000,
            PricingCatalog.WorstCaseMicroUsd("google", "gemini-4-carbon", 1_000_000, 10_000, at: At("2026-12-02T00:00:00")));
        // Providers without dated prices are unaffected by the date.
        Assert.Equal(2_000_000, PricingCatalog.CostMicroUsd("openai", "gpt-6.2-sol", 1_000_000, 0, 0, 0, requests: 4, at: At("2030-01-01T00:00:00")));
    }

    [Fact]
    public void The_cost_of_a_turn_uses_the_price_in_effect_when_it_is_recorded()
    {
        var snapshot = new InMemoryLedger().CreateManagedRun(Config(model: "gemini-4-carbon"), null).ConfigSnapshot!;
        var turn = new ModelInvocation(1, 1_000_000, 0, 0, "", 0, 0);
        Assert.Equal(2_000_000, AgentGatewayRules.CostOf(snapshot, turn, 1, At("2026-11-30T12:00:00")));
        Assert.Equal(4_000_000, AgentGatewayRules.CostOf(snapshot, turn, 1, At("2026-12-01T12:00:00")));
    }
}
