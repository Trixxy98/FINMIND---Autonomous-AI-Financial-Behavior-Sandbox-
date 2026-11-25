using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0019 step 1: one provider catalog drives configuration, prices, credentials, and what the app shows.
public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GROQ_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("GOOGLE_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void The_api_refuses_every_provider_credential_by_name_only(string name)
    {
        var problems = StartupChecks.Problems(n => n == name ? "secret-value-123" : null);
        Assert.Contains(problems, p => p.StartsWith(name, StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains("secret-value-123", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_provider_credential_is_refused_even_before_its_adapter_exists()
    {
        foreach (var name in ProviderCatalog.CredentialNames)
            Assert.NotEmpty(StartupChecks.Problems(n => n == name ? "x" : null));
        Assert.Superset(new HashSet<string> { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "GROQ_API_KEY",
            "GEMINI_API_KEY", "GOOGLE_API_KEY" }, ProviderCatalog.CredentialNames.ToHashSet());
        Assert.Subset(ProviderCatalog.CredentialNames.ToHashSet(), new HashSet<string> { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
            "OPENAI_API_KEY", "GROQ_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY" });
    }

    [Fact]
    public void Every_paid_model_has_a_price_and_every_provider_states_its_versions_and_live_status()
    {
        Assert.Equal(RunCatalogs.Models.Keys.Where(k => k != "mock").Order(), ProviderCatalog.Paid.Keys.Order());
        foreach (var (id, provider) in ProviderCatalog.Paid)
        {
            Assert.Equal(id, provider.Id);
            Assert.StartsWith($"{id}-pricing/", provider.PricingVersion);
            Assert.False(provider.VerifiedLive, $"{id} has not been run against its live API from this project");
            Assert.NotEmpty(provider.Prices);
            Assert.Equal(provider.Prices.Keys.Order(), RunCatalogs.Models[id].Order());
            Assert.All(provider.Prices.Values, p => Assert.True(p.Input > 0 && p.Output > 0));
            Assert.False(string.IsNullOrWhiteSpace(provider.Label));
        }
        Assert.True(ProviderCatalog.IsPaid("anthropic"));
        Assert.False(ProviderCatalog.IsPaid("mock"));
        Assert.False(ProviderCatalog.IsPaid("someone-else"));
    }

    [Fact]
    public void Prices_and_the_worst_case_come_from_the_runs_provider()
    {
        Assert.Equal(PricingCatalog.CostMicroUsd("anthropic", "claude-opus-5-5", 1_000_000, 0, 0, 0), 4_000_000);
        Assert.Equal(0, PricingCatalog.CostMicroUsd("mock", "finmind-mock-v1", 1_000_000, 1_000_000, 0, 0));
        Assert.Throws<InvalidOperationException>(() => PricingCatalog.CostMicroUsd("anthropic", "not-a-model", 1, 1, 0, 0));
        Assert.Equal(ProviderCatalog.Paid["anthropic"].PricingVersion, PricingCatalog.VersionFor("anthropic"));
        Assert.Equal("none", PricingCatalog.VersionFor("mock"));
    }
}
