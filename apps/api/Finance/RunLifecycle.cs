using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0008: managed run lifecycle and immutable configuration snapshot.
public enum RunStatus { Draft, Queued, Initializing, Running, Completed, Failed, Cancelled, BudgetExceeded }

public sealed record RunInfo(
    Guid RunId, bool Managed, string? Status, string? StatusReason, JsonObject? ConfigSnapshot, string? ConfigHash,
    DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    Guid? BatchId = null, int? BatchPosition = null); // ADR-0015: set at creation, never changed

public sealed class RunConfigException(IReadOnlyList<string> fields)
    : LedgerException(LedgerReasonCode.InvalidRunConfig, "Run configuration is invalid.")
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

public static class RunLifecycle
{
    public const string OpeningIdempotencyKey = "run-opening";

    private static readonly HashSet<(RunStatus From, RunStatus To)> Allowed =
    [
        (RunStatus.Draft, RunStatus.Queued),
        (RunStatus.Queued, RunStatus.Initializing),
        (RunStatus.Initializing, RunStatus.Running),
        (RunStatus.Running, RunStatus.Completed),
        (RunStatus.Running, RunStatus.BudgetExceeded),
        (RunStatus.Initializing, RunStatus.Failed),
        (RunStatus.Running, RunStatus.Failed),
        (RunStatus.Draft, RunStatus.Cancelled),
        (RunStatus.Queued, RunStatus.Cancelled),
        (RunStatus.Initializing, RunStatus.Cancelled),
        (RunStatus.Running, RunStatus.Cancelled)
    ];

    public static bool IsAllowed(RunStatus from, RunStatus to) => Allowed.Contains((from, to));

    public static bool RequiresReason(RunStatus to) => to is RunStatus.Failed or RunStatus.Cancelled or RunStatus.BudgetExceeded;

    public static string Wire(RunStatus status) => status switch
    {
        RunStatus.Draft => "DRAFT",
        RunStatus.Queued => "QUEUED",
        RunStatus.Initializing => "INITIALIZING",
        RunStatus.Running => "RUNNING",
        RunStatus.Completed => "COMPLETED",
        RunStatus.Failed => "FAILED",
        RunStatus.Cancelled => "CANCELLED",
        RunStatus.BudgetExceeded => "BUDGET_EXCEEDED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static RunStatus Parse(string wire) =>
        Enum.GetValues<RunStatus>().Single(status => Wire(status) == wire);

    // Validates the reason for a transition; returns the stored (trimmed, bounded) reason.
    public static string? NormalizeReason(RunStatus to, string? reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (RequiresReason(to) && trimmed is null)
            throw new LedgerValidationException(LedgerReasonCode.MissingRequiredField, $"A reason is required to move a run to {Wire(to)}.");
        return trimmed is { Length: > 500 } ? TextLimits.Cut(trimmed, 500) : trimmed;
    }

    public static void EnsureTransition(RunStatus? from, RunStatus to)
    {
        if (from is null)
            throw new LedgerValidationException(LedgerReasonCode.InvalidRunTransition, "Unmanaged runs have no lifecycle.");
        if (!IsAllowed(from.Value, to))
            throw new IdempotencyConflictException(LedgerReasonCode.InvalidRunTransition,
                $"Run cannot move from {Wire(from.Value)} to {Wire(to)}.");
    }

    // Money-moving and setup operations on managed runs (ADR-0008 "Finance operations by status").
    public static void EnsureActive(RunStatus? status, params RunStatus[] allowed)
    {
        if (status is { } current && !allowed.Contains(current))
            throw new LedgerValidationException(LedgerReasonCode.RunNotActive, $"Run is {Wire(current)}.");
    }

    public static readonly RunStatus[] MoneyMovement = [RunStatus.Running];
    public static readonly RunStatus[] Setup = [RunStatus.Initializing, RunStatus.Running];
}

// Server catalogs. Scenarios come from versioned files (ADR-0010, ScenarioCatalog).
public static class RunCatalogs
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Models = new Dictionary<string, IReadOnlySet<string>>
    {
        // ADR-0011 deterministic mock agents.
        ["mock"] = new HashSet<string> { "finmind-mock-v1", "finmind-mock-adversarial-v1", "finmind-mock-chaotic-v1" },
    }.Concat(ProviderCatalog.Paid.Select(p => KeyValuePair.Create(p.Key, (IReadOnlySet<string>)p.Value.Prices.Keys.ToHashSet())))
     .ToDictionary(); // ADR-0019: paid providers and their models come from the provider catalog.

    public const string ConfigSchemaVersion = "run-config/v2";
    public const string ToolsetVersion = "finmind-tools/v1";
    public const string PolicyVersion = "finance-policy/v1";
}

public static class RunConfiguration
{
    private static readonly string[] RequiredFields =
    [
        "objective", "scenarioId", "seed", "startingBalanceSen", "transactionLimitSen", "durationDays",
        "maxActions", "maxModelTokens", "maxWallTimeSeconds", "memoryMode", "agent"
    ];

    // run-config/v2 (ADR-0012) optional fields; defaults keep mock configurations short.
    private static readonly string[] OptionalFields = ["maxCostMicroUsd", "describeUnavailableCapabilities"];

    // "sampling" is the v1 name; it is accepted only as an empty object.
    private static readonly HashSet<string> AgentFields = ["provider", "modelId", "settings", "sampling"];
    private static readonly HashSet<string> AnthropicSettings = ["effort", "maxOutputTokensPerTurn"];

    // Returns the stored snapshot: researcher fields plus server-added reproducibility metadata.
    public static JsonObject Validate(JsonObject? input, string? codeRevision)
    {
        var errors = new List<string>();
        if (input is null) throw new RunConfigException(["body"]);

        errors.AddRange(input.Select(p => p.Key).Where(key => !RequiredFields.Contains(key) && !OptionalFields.Contains(key)));
        errors.AddRange(RequiredFields.Where(key => !input.ContainsKey(key)));

        var objective = Text(input, "objective", 1, 2_000, errors);
        var scenarioId = Text(input, "scenarioId", 1, 20, errors);
        var scenario = scenarioId is null ? null : ScenarioCatalog.Latest(scenarioId);
        if (scenarioId is not null && scenario is null) errors.Add("scenarioId");
        var seed = Integer(input, "seed", 0, int.MaxValue, errors);
        var starting = Integer(input, "startingBalanceSen", 1, 10_000_000_000, errors);
        var limit = Integer(input, "transactionLimitSen", 1, starting ?? 10_000_000_000, errors);
        var duration = Integer(input, "durationDays", 1, 365, errors);
        var maxActions = Integer(input, "maxActions", 1, 1_000, errors);
        var maxTokens = Integer(input, "maxModelTokens", 0, 5_000_000, errors);
        var wallTime = Integer(input, "maxWallTimeSeconds", 1, 86_400, errors);
        var memoryMode = Text(input, "memoryMode", 1, 30, errors);
        if (memoryMode is not null && memoryMode is not ("none" or "bounded_summary")) errors.Add("memoryMode");
        var maxCost = input.ContainsKey("maxCostMicroUsd") ? Integer(input, "maxCostMicroUsd", 0, 100_000_000, errors) : 0;
        var describeUnavailable = false;
        if (input.ContainsKey("describeUnavailableCapabilities"))
        {
            if (input["describeUnavailableCapabilities"] is JsonValue flag && flag.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                describeUnavailable = flag.GetValue<bool>();
            else errors.Add("describeUnavailableCapabilities");
        }

        string? provider = null, modelId = null;
        var settings = new JsonObject();
        if (input["agent"] is JsonObject agent)
        {
            errors.AddRange(agent.Select(p => p.Key).Where(key => !AgentFields.Contains(key)).Select(key => $"agent.{key}"));
            provider = Text(agent, "provider", 1, 50, errors, "agent.");
            modelId = Text(agent, "modelId", 1, 100, errors, "agent.");
            if (provider is not null && !RunCatalogs.Models.ContainsKey(provider)) errors.Add("agent.provider");
            else if (provider is not null && modelId is not null && !RunCatalogs.Models[provider].Contains(modelId)) errors.Add("agent.modelId");
            if (agent.ContainsKey("sampling") && agent["sampling"] is not JsonObject { Count: 0 })
                errors.Add("agent.sampling");

            var given = agent["settings"] as JsonObject;
            if (agent.ContainsKey("settings") && given is null) errors.Add("agent.settings");
            if (ProviderCatalog.IsPaid(provider))
            {
                // ADR-0012: effort is always explicit so it is recorded; sampling parameters are not offered.
                given ??= new JsonObject();
                errors.AddRange(given.Select(p => p.Key).Where(key => !AnthropicSettings.Contains(key)).Select(key => $"agent.settings.{key}"));
                var effort = Text(given, "effort", 1, 10, errors, "agent.settings.");
                if (!given.ContainsKey("effort") || (effort is not null && !ProviderCatalog.Paid[provider!].Efforts.Contains(effort)))
                    errors.Add("agent.settings.effort");
                var maxOutput = given.ContainsKey("maxOutputTokensPerTurn")
                    ? Integer(given, "maxOutputTokensPerTurn", 1_024, 16_000, errors, "agent.settings.") : 16_000;
                settings = new JsonObject { ["effort"] = effort, ["maxOutputTokensPerTurn"] = maxOutput };
                if (maxCost is null or 0) errors.Add("maxCostMicroUsd");
                if (memoryMode is not null and not "none") errors.Add("memoryMode");
            }
            else if (provider == "mock")
            {
                if (given is { Count: > 0 }) errors.Add("agent.settings");
                if (maxCost is > 0) errors.Add("maxCostMicroUsd");
            }
            // Only the mock agent may run with a zero token budget.
            if (provider is not null && RunCatalogs.Models.ContainsKey(provider) && provider != "mock" && maxTokens == 0)
                errors.Add("maxModelTokens");
        }
        else if (input.ContainsKey("agent"))
        {
            errors.Add("agent");
        }

        if (errors.Count > 0) throw new RunConfigException(errors.Distinct().Order(StringComparer.Ordinal).ToArray());

        var prompt = ProviderCatalog.IsPaid(provider) ? PromptCatalog.Current : null;
        return new JsonObject
        {
            ["objective"] = objective,
            ["scenarioId"] = scenarioId,
            ["seed"] = seed,
            ["startingBalanceSen"] = starting,
            ["transactionLimitSen"] = limit,
            ["durationDays"] = duration,
            ["maxActions"] = maxActions,
            ["maxModelTokens"] = maxTokens,
            ["maxWallTimeSeconds"] = wallTime,
            ["maxCostMicroUsd"] = maxCost,
            ["memoryMode"] = memoryMode,
            ["describeUnavailableCapabilities"] = describeUnavailable,
            ["agent"] = AgentSnapshot(provider, modelId, settings),
            ["configSchemaVersion"] = RunCatalogs.ConfigSchemaVersion,
            ["scenarioVersion"] = scenario!.VersionLabel,
            ["scenarioHash"] = scenario.FileHash,
            ["toolsetVersion"] = RunCatalogs.ToolsetVersion,
            ["policyVersion"] = RunCatalogs.PolicyVersion,
            ["reasonCodeCatalog"] = LedgerReasonCodes.CatalogVersion,
            ["promptVersion"] = prompt?.Version ?? "none",
            ["promptHash"] = prompt?.Hash,
            ["pricingVersion"] = PricingCatalog.VersionFor(provider),
            ["codeRevision"] = string.IsNullOrWhiteSpace(codeRevision) ? "unknown" : codeRevision.Trim()
        };
    }

    // ADR-0019: providers with an adapter version record it, the input-token bound, and that sampling is the provider's.
    private static JsonObject AgentSnapshot(string? provider, string? modelId, JsonObject settings)
    {
        var agent = new JsonObject { ["provider"] = provider, ["modelId"] = modelId, ["settings"] = settings };
        if (provider is not null && ProviderCatalog.Paid.TryGetValue(provider, out var info) && info.Adapter is not null)
        {
            agent["adapter"] = info.Adapter;
            agent["tokenBound"] = info.TokenBound;
            agent["sampling"] = "provider-defaults";
        }
        return agent;
    }

    // In-memory hash for tests. PostgreSQL computes the canonical hash over its own JSONB text (ADR-0008).
    public static string InMemoryHash(JsonObject snapshot) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("finmind-run-config-v1\n" + snapshot.ToJsonString())));

    public static long StartingBalance(JsonObject snapshot) => snapshot["startingBalanceSen"]!.GetValue<long>();
    public static long TransactionLimit(JsonObject snapshot) => snapshot["transactionLimitSen"]!.GetValue<long>();
    public static long Seed(JsonObject snapshot) => snapshot["seed"]!.GetValue<long>();
    public static int DurationDays(JsonObject snapshot) => (int)snapshot["durationDays"]!.GetValue<long>();

    // ADR-0010: the run's scenario must be the exact file recorded in its snapshot.
    public static ScenarioDefinition ResolveScenario(JsonObject snapshot)
    {
        var version = snapshot["scenarioVersion"]?.GetValue<string>();
        var scenario = version is null ? null : ScenarioCatalog.Find(version);
        if (scenario is null) throw new RunConfigException(["scenarioVersion"]);
        if (scenario.FileHash != snapshot["scenarioHash"]?.GetValue<string>()) throw new RunConfigException(["scenarioHash"]);
        return scenario;
    }

    private static string? Text(JsonObject source, string key, int min, int max, List<string> errors, string prefix = "")
    {
        if (!source.ContainsKey(key)) return null;
        if (source[key] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) && text.Length >= min && text.Length <= max)
            return text;
        errors.Add(prefix + key);
        return null;
    }

    private static long? Integer(JsonObject source, string key, long min, long max, List<string> errors, string prefix = "")
    {
        if (!source.ContainsKey(key)) return null;
        if (source[key] is JsonValue value && TryGetInteger(value, out var number) && number >= min && number <= max)
            return number;
        errors.Add(prefix + key);
        return null;
    }

    // Accepts JSON integers only: no strings, booleans, or fractions.
    private static bool TryGetInteger(JsonValue value, out long number)
    {
        number = 0;
        if (value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (value.TryGetValue<int>(out var small)) { number = small; return true; }
        return false;
    }
}
