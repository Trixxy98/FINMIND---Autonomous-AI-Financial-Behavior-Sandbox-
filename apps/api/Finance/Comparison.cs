using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace Finmind.Api.Finance;

public enum ComparisonUnit { Sen, Count, MicroUsd }

// ADR-0015 section 2. Descriptive only: no p-values, and an interval only from n >= 5.
public static class Comparison
{
    public const string Version = "finmind-compare/v2"; // v2 (ADR-0018): adds outcomes
    public const int MinimumForInterval = 5;

    // Two-sided 95% t critical values for 4 to 19 degrees of freedom (batches hold at most 20 runs).
    private static readonly double[] T95 = [2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093];

    public static readonly string[] ResearchFields =
    [
        "objective", "scenarioId", "seed", "startingBalanceSen", "transactionLimitSen", "durationDays", "maxActions",
        "maxModelTokens", "maxWallTimeSeconds", "maxCostMicroUsd", "memoryMode", "describeUnavailableCapabilities"
    ];

    private static readonly HashSet<string> ScenarioBound = ["scenarioVersion", "scenarioHash"];

    public static readonly string[] MetadataFields =
    [
        "configSchemaVersion", "scenarioVersion", "scenarioHash", "toolsetVersion", "policyVersion", "reasonCodeCatalog",
        "promptVersion", "promptHash", "pricingVersion", "codeRevision"
    ];

    // Integer values: the mean is exact decimal arithmetic, rounded half to even to two places.
    // ADR-0018: per outcome and group, where it happened over exposed runs; unexposed runs are listed apart. No score.
    public static JsonArray SummarizeOutcomes(IReadOnlyList<(JsonNode BatchId, IReadOnlyList<JsonObject> Runs)> groups)
    {
        var order = new List<string>();
        var first = new Dictionary<string, JsonObject>();
        foreach (var (_, runs) in groups)
            foreach (var run in runs)
                foreach (var outcome in run["outcomes"]!.AsArray().Select(o => o!.AsObject()))
                    if (first.TryAdd((string)outcome["id"]!, outcome)) order.Add((string)outcome["id"]!);

        var result = new JsonArray();
        foreach (var id in order)
        {
            var perGroup = new JsonArray();
            foreach (var (batchId, runs) in groups)
            {
                var found = runs.Select(r => (RunId: r["runId"]!.DeepClone(), Outcome: r["outcomes"]!.AsArray()
                    .Select(o => o!.AsObject()).FirstOrDefault(o => (string)o["id"]! == id))).Where(x => x.Outcome is not null).ToList();
                var exposed = found.Where(x => x.Outcome!["exposed"]!.GetValue<bool>()).ToList();
                long ValueOf(JsonObject o) => o["value"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
                var group = new JsonObject
                {
                    ["batchId"] = batchId.DeepClone(), ["runs"] = found.Count, ["exposed"] = exposed.Count,
                    ["happenedIn"] = exposed.Count(x => ValueOf(x.Outcome!) > 0),
                    ["notExposed"] = new JsonArray(found.Where(x => !x.Outcome!["exposed"]!.GetValue<bool>()).Select(x => x.RunId).ToArray()),
                    ["values"] = new JsonArray(exposed.Select(x => (JsonNode)new JsonObject
                    {
                        ["runId"] = x.RunId.DeepClone(), ["value"] = ValueOf(x.Outcome!),
                        ["denominator"] = x.Outcome!["denominator"]?.DeepClone()
                    }).ToArray())
                };
                if (exposed.Any(x => x.Outcome!["denominator"] is not null))
                    group["completeIn"] = exposed.Count(x => x.Outcome!["denominator"] is JsonValue d && d.TryGetValue<long>(out var total)
                        && total > 0 && ValueOf(x.Outcome!) == total);
                perGroup.Add(group);
            }
            var definition = first[id];
            result.Add(new JsonObject
            {
                ["id"] = id, ["category"] = definition["category"]!.DeepClone(), ["unit"] = definition["unit"]!.DeepClone(),
                ["definition"] = definition["definition"]!.DeepClone(), ["groups"] = perGroup
            });
        }
        return result;
    }

    public static JsonObject Summarize(IReadOnlyList<long> values, ComparisonUnit unit)
    {
        var summary = new JsonObject { ["unit"] = unit.ToString().ToLowerInvariant(), ["n"] = values.Count };
        if (values.Count == 0)
        {
            summary["mean"] = null; summary["min"] = null; summary["max"] = null; summary["sd"] = null; summary["ci95"] = null;
            return summary;
        }
        var mean = Math.Round(values.Sum(v => (decimal)v) / values.Count, 2, MidpointRounding.ToEven);
        summary["mean"] = mean.ToString("0.00", CultureInfo.InvariantCulture);
        summary["min"] = values.Min();
        summary["max"] = values.Max();
        Spread(summary, values.Select(v => (double)v).ToList(), 2);
        return summary;
    }

    public static JsonObject SummarizeRates(IReadOnlyList<double?> values)
    {
        var applicable = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        var summary = new JsonObject { ["unit"] = "rate", ["n"] = applicable.Count, ["notApplicable"] = values.Count - applicable.Count };
        if (applicable.Count == 0)
        {
            summary["mean"] = null; summary["min"] = null; summary["max"] = null; summary["sd"] = null; summary["ci95"] = null;
            return summary;
        }
        summary["mean"] = Math.Round(applicable.Average(), 4);
        summary["min"] = applicable.Min();
        summary["max"] = applicable.Max();
        Spread(summary, applicable, 4);
        return summary;
    }

    private static void Spread(JsonObject summary, List<double> values, int digits)
    {
        if (values.Count < 2)
        {
            summary["sd"] = null;
            summary["ci95"] = null;
            return;
        }
        var mean = values.Average();
        var sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        summary["sd"] = Math.Round(sd, digits);
        if (values.Count < MinimumForInterval || values.Count - 1 - 4 >= T95.Length)
        {
            summary["ci95"] = null;
            return;
        }
        var half = T95[values.Count - 1 - 4] * sd / Math.Sqrt(values.Count);
        summary["ci95"] = new JsonObject { ["low"] = Math.Round(mean - half, digits), ["high"] = Math.Round(mean + half, digits) };
    }

    // Null when the run is compared; otherwise why it is left out.
    public static string? ExclusionReason(bool researchEligible, string? status, string? statusReason)
    {
        if (!researchEligible) return "development run";
        if (status is null || !AgentGatewayRules.IsTerminal(RunLifecycle.Parse(status))) return "not finished";
        if (status == "CANCELLED") return "cancelled";
        if (status == "FAILED" && statusReason is { } reason
            && (reason.StartsWith("runtime:", StringComparison.Ordinal) || reason == AgentGatewayRules.SessionExpiredReason))
            return "platform failure";
        return null;
    }

    private static IReadOnlyDictionary<string, string> Fields(JsonObject snapshot, IEnumerable<string> names)
    {
        var fields = names.ToDictionary(name => name, name => snapshot[name]?.ToJsonString() ?? "null");
        if (names.Contains("scenarioId") && snapshot["agent"] is JsonObject agent)
        {
            fields["agent.provider"] = agent["provider"]?.ToJsonString() ?? "null";
            fields["agent.modelId"] = agent["modelId"]?.ToJsonString() ?? "null";
            if (agent["settings"] is JsonObject settings)
                foreach (var (key, value) in settings) fields[$"agent.settings.{key}"] = value?.ToJsonString() ?? "null";
        }
        return fields;
    }

    // betweenGroups: fields where two groups share no value. withinGroups: fields that vary inside a group.
    public static JsonObject Differences(IReadOnlyList<IReadOnlyList<JsonObject>> groups)
    {
        var research = groups.Select(g => g.Select(s => Fields(s, ResearchFields)).ToList()).ToList();
        var names = research.SelectMany(g => g).SelectMany(f => f.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        HashSet<string> Values(List<IReadOnlyDictionary<string, string>> group, string name) =>
            group.Select(f => f.GetValueOrDefault(name, "absent")).ToHashSet();

        var between = names.Where(name => research.Where(g => g.Count > 0).Select(g => Values(g, name)).ToList() is var sets
            && sets.SelectMany((a, i) => sets.Skip(i + 1).Select(b => !a.Overlaps(b))).Any(disjoint => disjoint)).ToList();
        var within = research.Select(g => new JsonArray(names.Where(name => Values(g, name).Count > 1).Select(n => (JsonNode)n).ToArray()));
        var runs = groups.SelectMany(g => g).ToList();
        var metadata = runs.Select(s => Fields(s, MetadataFields)).ToList();
        var scenarios = runs.Select(s => s["scenarioId"]?.ToJsonString() ?? "null").ToList();
        // A scenario's version and hash follow its ID. They are a warning only when one scenario ID has more than one.
        bool Differs(string name) => ScenarioBound.Contains(name)
            ? metadata.Select((f, i) => (Scenario: scenarios[i], Value: f[name])).GroupBy(x => x.Scenario)
                .Any(g => g.Select(x => x.Value).Distinct().Count() > 1)
            : metadata.Select(f => f[name]).Distinct().Count() > 1;
        var metadataDiffers = MetadataFields.Where(Differs);
        return new JsonObject
        {
            ["betweenGroups"] = new JsonArray(between.Select(n => (JsonNode)n).ToArray()),
            ["withinGroups"] = new JsonArray(within.Select(w => (JsonNode)w).ToArray()),
            ["metadata"] = new JsonArray(metadataDiffers.Select(n => (JsonNode)n).ToArray())
        };
    }

    // ADR-0015: runs are started in a seeded random order; the seed is stored, so the order can be checked.
    public static int[] StartOrder(int count, long orderSeed)
    {
        var random = new Xoshiro256StarStar(SHA256.HashData(Encoding.UTF8.GetBytes($"finmind-batch-order|{orderSeed}")));
        var order = Enumerable.Range(0, count).ToArray();
        for (var i = count - 1; i > 0; i--)
        {
            var j = (int)(random.Next() % (ulong)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    // Metrics compared, read from finmind-metrics/v1.
    public static readonly (string Name, string[] Path, ComparisonUnit? Unit)[] Metrics =
    [
        ("finalNetWorthSen", ["finalNetWorthSen", "value"], ComparisonUnit.Sen),
        ("netWorthChangeSen", ["netWorthChangeSen", "value"], ComparisonUnit.Sen),
        ("fundsReceivedSen", ["fundsReceivedSen", "value"], ComparisonUnit.Sen),
        ("fundsSpentSen", ["fundsSpentSen", "value"], ComparisonUnit.Sen),
        ("unauthorizedAttemptRate", ["unauthorizedAttemptRate", "value"], null),
        ("policyComplianceRate", ["policyComplianceRate", "value"], null),
        ("fundingRequests", ["fundingRequests", "count"], ComparisonUnit.Count),
        ("fundingAmountAskedSen", ["fundingRequests", "amountAskedSen"], ComparisonUnit.Sen),
        ("insufficientFunds", ["mistakes", "insufficientFunds"], ComparisonUnit.Count),
        ("malformedAttempts", ["mistakes", "malformed"], ComparisonUnit.Count),
        ("integrityViolations", ["integrityViolations", "value"], ComparisonUnit.Count),
        ("actions", ["budgets", "actions", "used"], ComparisonUnit.Count),
        ("modelTokens", ["budgets", "modelTokens", "used"], ComparisonUnit.Count),
        ("costMicroUsd", ["budgets", "costMicroUsd", "recorded"], ComparisonUnit.MicroUsd),
    ];

    public static JsonNode? Read(JsonObject metrics, string[] path) =>
        path.Aggregate((JsonNode?)metrics["metrics"], (node, key) => node?[key]);
}

// ADR-0015: batch rows (PostgreSQL). Append-only; the API role may only insert and read.
public sealed class BatchStore(NpgsqlDataSource dataSource)
{
    public Guid Insert(string? label, JsonObject config, int count, string seedMode, long orderSeed, int[] startOrder, long[]? seeds = null)
    {
        var id = Guid.NewGuid();
        InsertWithId(id, label, config, count, seedMode, orderSeed, startOrder, seeds);
        return id;
    }

    public void InsertWithId(Guid id, string? label, JsonObject config, int count, string seedMode, long orderSeed, int[] startOrder,
        long[]? seeds = null)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "INSERT INTO run_batches (id, label, request_config, run_count, seed_mode, seeds, order_seed, start_order) " +
            "VALUES (@id, @label, @config, @count, @mode, @seeds, @order_seed, @start_order)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("label", NpgsqlTypes.NpgsqlDbType.Text, (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("config", NpgsqlTypes.NpgsqlDbType.Jsonb, config.ToJsonString());
        command.Parameters.AddWithValue("count", count);
        command.Parameters.AddWithValue("mode", seedMode);
        command.Parameters.AddWithValue("seeds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint, (object?)seeds ?? DBNull.Value);
        command.Parameters.AddWithValue("order_seed", orderSeed);
        command.Parameters.AddWithValue("start_order", startOrder);
        command.ExecuteNonQuery();
    }

    public JsonObject List(int limit)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT id, created_at, label, run_count, seed_mode, request_config->>'scenarioId', request_config->'agent'->>'provider', " +
            "request_config->'agent'->>'modelId' FROM run_batches ORDER BY created_at DESC, id DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("limit", limit);
        using var reader = command.ExecuteReader();
        var batches = new JsonArray();
        while (reader.Read())
            batches.Add(new JsonObject
            {
                ["batchId"] = reader.GetGuid(0), ["createdAt"] = reader.GetFieldValue<DateTimeOffset>(1),
                ["label"] = reader.IsDBNull(2) ? null : reader.GetString(2), ["count"] = reader.GetInt32(3),
                ["seedMode"] = reader.GetString(4), ["scenarioId"] = reader.IsDBNull(5) ? null : reader.GetString(5),
                ["agent"] = new JsonObject
                {
                    ["provider"] = reader.IsDBNull(6) ? null : reader.GetString(6), ["modelId"] = reader.IsDBNull(7) ? null : reader.GetString(7)
                }
            });
        return new JsonObject { ["batches"] = batches };
    }

    public JsonObject? Get(Guid batchId)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
        JsonObject batch;
        using (var command = new NpgsqlCommand(
            "SELECT created_at, label, request_config::text, run_count, seed_mode, seeds, order_seed, start_order FROM run_batches WHERE id = @id",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", batchId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            batch = new JsonObject
            {
                ["batchId"] = batchId, ["createdAt"] = reader.GetFieldValue<DateTimeOffset>(0),
                ["label"] = reader.IsDBNull(1) ? null : reader.GetString(1), ["config"] = JsonNode.Parse(reader.GetString(2)),
                ["count"] = reader.GetInt32(3), ["seedMode"] = reader.GetString(4),
                ["seeds"] = reader.IsDBNull(5) ? null : new JsonArray(reader.GetFieldValue<long[]>(5).Select(s => (JsonNode)s).ToArray()),
                ["orderSeed"] = reader.GetInt64(6),
                ["startOrder"] = new JsonArray(reader.GetFieldValue<int[]>(7).Select(p => (JsonNode)p).ToArray())
            };
        }
        var runs = new JsonArray();
        using (var command = new NpgsqlCommand(
            "SELECT id, batch_position, status, status_reason, queued_at FROM experiment_runs WHERE batch_id = @id ORDER BY batch_position",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", batchId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                runs.Add(new JsonObject
                {
                    ["runId"] = reader.GetGuid(0), ["batchPosition"] = reader.GetInt32(1), ["status"] = reader.GetString(2),
                    ["statusReason"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["queuedAt"] = reader.IsDBNull(4) ? null : JsonValue.Create(reader.GetFieldValue<DateTimeOffset>(4))
                });
        }
        transaction.Commit();
        batch["runs"] = runs;
        batch["complete"] = runs.Count == (int)batch["count"]!;
        return batch;
    }
}
