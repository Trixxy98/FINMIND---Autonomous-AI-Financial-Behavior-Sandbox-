using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0017 step 3: a shorter worker session for crash drills, within fixed bounds; the default stays 10 minutes.
public sealed class SessionLifetimeTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("14")]
    [InlineData("601")]
    [InlineData("abc")]
    [InlineData("20.5")]
    [InlineData("-30")]
    public void Out_of_range_or_malformed_lifetimes_stop_startup(string value)
    {
        var problems = StartupChecks.Problems(name => name == "FINMIND_SESSION_LIFETIME_SECONDS" ? value : null);
        Assert.Contains(problems, p => p.StartsWith("FINMIND_SESSION_LIFETIME_SECONDS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData("15", 15)]
    [InlineData("600", 600)]
    public void Valid_lifetimes_are_used_and_the_default_is_ten_minutes(string? value, int seconds)
    {
        Assert.Empty(StartupChecks.Problems(name => name == "FINMIND_SESSION_LIFETIME_SECONDS" ? value : null));
        Assert.Equal(TimeSpan.FromSeconds(seconds), AgentGatewayRules.SessionLifetimeFrom(value));
    }

    [Fact]
    public void A_gateway_expires_sessions_after_its_configured_lifetime()
    {
        var ledger = new InMemoryLedger();
        var time = new FakeTime();
        var gateway = new ToolGateway(ledger, time, TimeSpan.FromSeconds(20));
        var run = ledger.CreateManagedRun(new JsonObject
        {
            ["objective"] = "Manage funds.", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100_000,
            ["transactionLimitSen"] = 50_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = 0,
            ["maxWallTimeSeconds"] = 3_600, ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        }, null).RunId;
        ledger.TransitionRun(run, RunStatus.Queued, null, FinanceEvents.InternalApiSource);
        var claim = gateway.Claim()!.Value;
        ledger.ActivateRun(run, FinanceEvents.WorkerSource);
        time.Now += TimeSpan.FromSeconds(15);
        Assert.Equal(run, gateway.ResolveSession(claim.Token)); // still alive, and the use slides it
        time.Now += TimeSpan.FromSeconds(21);
        Assert.Null(gateway.ResolveSession(claim.Token));
        gateway.ReapExpiredSessions();
        Assert.Equal(("FAILED", "Worker session expired."), (ledger.GetRun(run).Status, ledger.GetRun(run).StatusReason));
    }
}
