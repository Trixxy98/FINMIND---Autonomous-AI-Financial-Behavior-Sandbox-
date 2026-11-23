using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0013 A3: the local trust boundary in front of every route.
public sealed class RequestGuardTests
{
    private const string Researcher = "researcher-token-0123456789-abcdefghij";
    private static readonly RequestGuardOptions Options = RequestGuardOptions.Create(Researcher, null);

    [Theory]
    [InlineData("/internal/runs", "127.0.0.1:5080", null, Researcher, null)]
    [InlineData("/internal/runs", "LOCALHOST:5080", "http://localhost:5173", Researcher, null)]
    [InlineData("/internal/runs", "127.0.0.1:5080", null, null, 401)]
    [InlineData("/internal/runs", "127.0.0.1:5080", null, Researcher + "x", 401)]
    [InlineData("/internal/runs", "attacker.example:5080", null, Researcher, 403)]
    [InlineData("/internal/runs", "127.0.0.1", null, Researcher, 403)]
    [InlineData("/internal/runs", "", null, Researcher, 403)]
    [InlineData("/internal/runs", "127.0.0.1:5080", "null", Researcher, 403)]
    [InlineData("/internal/runs", "127.0.0.1:5080", "http://127.0.0.1:5173.attacker.example", Researcher, 403)]
    [InlineData("/health", "127.0.0.1:5080", null, null, null)]
    [InlineData("/health", "attacker.example", null, null, 403)]
    [InlineData("/internal/worker/runs/claim", "127.0.0.1:5080", null, null, null)]
    [InlineData("/INTERNAL/WORKER/runs/claim", "127.0.0.1:5080", null, null, null)]
    [InlineData("/internal/worker/runs/claim", "attacker.example:5080", null, null, 403)]
    [InlineData("/internal/workers", "127.0.0.1:5080", null, null, 401)]
    [InlineData("/healthz", "127.0.0.1:5080", null, null, 401)]
    public void Decides(string path, string host, string? origin, string? bearer, int? expected) =>
        Assert.Equal(expected, RequestGuard.Check(path, host, origin, bearer, Options));

    [Fact]
    public void Without_a_researcher_token_researcher_routes_stay_closed() =>
        Assert.Equal(401, RequestGuard.Check("/internal/runs", "127.0.0.1:5080", null, "", RequestGuardOptions.Create(null, null)));

    [Fact]
    public void Configured_origins_replace_the_defaults()
    {
        var options = RequestGuardOptions.Create(Researcher, "http://127.0.0.1:4000");
        Assert.Null(RequestGuard.Check("/internal/runs", "127.0.0.1:5080", "http://127.0.0.1:4000", Researcher, options));
        Assert.Equal(403, RequestGuard.Check("/internal/runs", "127.0.0.1:5080", "http://127.0.0.1:5173", Researcher, options));
    }

    [Theory]
    [InlineData("FINMIND_RESEARCHER_TOKEN", "short", "FINMIND_RESEARCHER_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY", "sk-ant-not-real", "ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN", "x", "ANTHROPIC_AUTH_TOKEN")]
    [InlineData("FINMIND_WORKER_TOKEN", Researcher, "FINMIND_WORKER_TOKEN")]
    public void Unsafe_configuration_stops_startup(string key, string value, string named)
    {
        var settings = new Dictionary<string, string?> { ["FINMIND_RESEARCHER_TOKEN"] = Researcher, [key] = value };
        var problem = Assert.Single(StartupChecks.Problems(name => settings.GetValueOrDefault(name)));
        Assert.Contains(named, problem);
        Assert.DoesNotContain(value, problem);
    }

    [Fact]
    public void Safe_configuration_starts() =>
        Assert.Empty(StartupChecks.Problems(name => name == "FINMIND_RESEARCHER_TOKEN" ? Researcher : null));

    [PostgresFact]
    public void The_owner_role_is_refused_and_the_api_role_is_accepted()
    {
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        using var api = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        Assert.NotNull(StartupChecks.PrivilegedRole(owner));
        Assert.Null(StartupChecks.PrivilegedRole(api));
    }
}
