using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Finmind.Api.Finance;

// ADR-0013 A3: the single-researcher local trust boundary, checked before routing.
public sealed record RequestGuardOptions(IReadOnlySet<string> Origins, byte[]? ResearcherTokenHash)
{
    // A local Host only, so a rebound DNS name never reaches a route.
    public static readonly IReadOnlySet<string> Hosts =
        new HashSet<string>(["127.0.0.1:5080", "localhost:5080"], StringComparer.OrdinalIgnoreCase);

    public static readonly string[] DefaultOrigins = ["http://127.0.0.1:5173", "http://localhost:5173"];

    public bool ResearcherConfigured => ResearcherTokenHash is not null;

    // allowedOrigins: comma-separated FINMIND_ALLOWED_ORIGINS, replacing the defaults (the Vite dev server).
    public static RequestGuardOptions Create(string? researcherToken, string? allowedOrigins) => new(
        new HashSet<string>(string.IsNullOrWhiteSpace(allowedOrigins)
            ? DefaultOrigins
            : allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal),
        researcherToken is { Length: >= StartupChecks.MinTokenLength } ? Hash(researcherToken) : null);

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

public static class RequestGuard
{
    // Null to continue; otherwise the status to answer with.
    public static int? Check(string path, string host, string? origin, string? bearer, RequestGuardOptions options)
    {
        if (!RequestGuardOptions.Hosts.Contains(host)) return 403;
        // Browsers send Origin on cross-origin requests; a page elsewhere cannot use the researcher's routes.
        if (!string.IsNullOrEmpty(origin) && !options.Origins.Contains(origin)) return 403;
        // Routing ignores case, so these do too. Worker routes check their own credentials.
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/internal/worker/", StringComparison.OrdinalIgnoreCase)) return null;
        if (options.ResearcherTokenHash is not { } expected || string.IsNullOrEmpty(bearer)) return 401;
        return CryptographicOperations.FixedTimeEquals(RequestGuardOptions.Hash(bearer), expected) ? null : 401;
    }
}

public static class StartupChecks
{
    public const int MinTokenLength = 32;

    // Names only: values never appear in messages or logs.
    public static IReadOnlyList<string> Problems(Func<string, string?> setting)
    {
        var problems = new List<string>();
        var researcher = setting("FINMIND_RESEARCHER_TOKEN");
        if (!string.IsNullOrEmpty(researcher) && researcher.Length < MinTokenLength)
            problems.Add($"FINMIND_RESEARCHER_TOKEN must be at least {MinTokenLength} characters.");
        if (!string.IsNullOrEmpty(researcher) && researcher == setting("FINMIND_WORKER_TOKEN"))
            problems.Add("FINMIND_WORKER_TOKEN must differ from the researcher token.");
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN" })
            if (!string.IsNullOrEmpty(setting(name)))
                problems.Add($"{name} is set; provider credentials belong to the worker only (ADR-0012).");
        return problems;
    }

    // The API must not run as a role that owns the schema or can bypass its triggers (ADR-0004).
    public static string? PrivilegedRole(NpgsqlDataSource dataSource)
    {
        try
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand(
                "SELECT r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolbypassrls OR EXISTS (" +
                "SELECT 1 FROM pg_tables t WHERE t.schemaname = 'public' AND t.tableowner = current_user) " +
                "FROM pg_roles r WHERE r.rolname = current_user", connection);
            return command.ExecuteScalar() is false
                ? null
                : "The API database role is privileged or owns tables; use the restricted finmind_api role (ADR-0004).";
        }
        catch (NpgsqlException)
        {
            return "The database role could not be checked; start PostgreSQL first.";
        }
    }
}
