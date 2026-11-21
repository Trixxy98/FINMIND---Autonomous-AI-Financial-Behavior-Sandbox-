using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Finmind.Api.Finance;

// ADR-0010: versioned scenario files, seeded materialization, and the virtual clock.

// xoshiro256** (Blackman and Vigna). Implemented here so results never depend on System.Random.
public sealed class Xoshiro256StarStar
{
    public const string Algorithm = "prng/xoshiro256starstar-v1";
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256StarStar(ReadOnlySpan<byte> state32)
    {
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(state32[..8]);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(state32[8..16]);
        _s2 = BinaryPrimitives.ReadUInt64LittleEndian(state32[16..24]);
        _s3 = BinaryPrimitives.ReadUInt64LittleEndian(state32[24..32]);
    }

    public static Xoshiro256StarStar ForScenario(string scenarioId, int version, long seed) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"finmind-scenario-v1|{scenarioId}|{version.ToString(CultureInfo.InvariantCulture)}|{seed.ToString(CultureInfo.InvariantCulture)}")));

    public ulong Next()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    // min + step * (next mod count). Modulo bias is accepted and versioned (ADR-0010).
    public long NextInRange(long min, long max, long step)
    {
        var count = (ulong)((max - min) / step + 1);
        return min + step * (long)(Next() % count);
    }
}

public sealed record ScenarioMerchant(string Key, string Label);

public sealed record ScenarioEventDefinition(
    string Key, int Day, string Kind, string? From, string? Text,
    string? Merchant, long? FixedAmountSen, (long Min, long Max, long Step)? AmountRange,
    int? DueDay, string? InvoiceReference, string? Description);

public sealed record ScenarioDefinition(
    string Id, int Version, string Title, string RecommendedObjective, DateOnly VirtualStartDate,
    IReadOnlyList<ScenarioMerchant> Merchants, IReadOnlyList<ScenarioEventDefinition> Events, string FileHash)
{
    public string VersionLabel => $"{Id}/v{Version}";
}

// One materialized event. Payload holds resolved values: amounts drawn, text placeholders filled.
public sealed record MaterializedScenarioEvent(string Key, int Day, string Kind, JsonObject Payload);

public sealed class ScenarioValidationException(string file, IReadOnlyList<string> problems)
    : Exception($"Scenario file {file} is invalid: {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public static class ScenarioCatalog
{
    public const string Schema = "scenario/v1";
    private static readonly Lazy<IReadOnlyDictionary<(string Id, int Version), ScenarioDefinition>> DefaultCatalog =
        new(() => LoadDirectory(FindDirectory()));

    // Every scenario file, by (id, version). Loading validates all files and fails on the first invalid one.
    public static IReadOnlyDictionary<(string Id, int Version), ScenarioDefinition> All => DefaultCatalog.Value;

    // The highest version of each id, offered for new runs.
    public static ScenarioDefinition? Latest(string id) =>
        All.Values.Where(s => s.Id == id).MaxBy(s => s.Version);

    public static ScenarioDefinition? Find(string versionLabel) =>
        All.Values.FirstOrDefault(s => s.VersionLabel == versionLabel);

    // FINMIND_SCENARIOS_DIR, else the nearest "scenarios" directory above the working or base directory.
    public static string FindDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FINMIND_SCENARIOS_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "scenarios");
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.json").Any()) return candidate;
            }
        }
        throw new DirectoryNotFoundException("No scenarios directory found. Set FINMIND_SCENARIOS_DIR.");
    }

    public static IReadOnlyDictionary<(string Id, int Version), ScenarioDefinition> LoadDirectory(string directory)
    {
        var catalog = new Dictionary<(string, int), ScenarioDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var definition = Parse(Path.GetFileName(file), File.ReadAllBytes(file));
            if (!catalog.TryAdd((definition.Id, definition.Version), definition))
                throw new ScenarioValidationException(Path.GetFileName(file), ["duplicate id and version"]);
        }
        return catalog;
    }

    private static readonly HashSet<string> TopLevelFields =
        ["schema", "id", "version", "title", "researcherNotes", "recommendedObjective", "virtualStartDate", "merchants", "events"];
    private static readonly HashSet<string> MerchantFields = ["key", "label"];
    private static readonly HashSet<string> MessageFields = ["key", "day", "kind", "from", "text"];
    private static readonly HashSet<string> ObligationFields =
        ["key", "day", "kind", "merchant", "amountSen", "dueDay", "invoiceReference", "description"];
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderPattern = new(@"\{([a-z][a-z0-9_]{0,39})\.amount\}", RegexOptions.CultureInvariant);

    public static ScenarioDefinition Parse(string fileName, byte[] content)
    {
        var problems = new List<string>();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(content)?.AsObject() ?? throw new FormatException();
        }
        catch (Exception)
        {
            throw new ScenarioValidationException(fileName, ["not a JSON object"]);
        }

        foreach (var key in root.Select(p => p.Key).Where(k => !TopLevelFields.Contains(k))) problems.Add($"unknown field {key}");
        if (Str(root, "schema") != Schema) problems.Add("schema must be scenario/v1");
        var id = Str(root, "id");
        if (id is null || !Regex.IsMatch(id, "^S[0-9]{2}$")) problems.Add("id must look like S01");
        var version = Int(root, "version");
        if (version is null or < 1) problems.Add("version must be a positive integer");
        if (id is not null && version is not null && fileName != $"{id}.v{version}.json") problems.Add("file name must be <id>.v<version>.json");
        var title = Str(root, "title");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) problems.Add("title required (max 200)");
        if (Str(root, "researcherNotes") is not { Length: <= 4000 }) problems.Add("researcherNotes required (max 4000)");
        var objective = Str(root, "recommendedObjective");
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 2000) problems.Add("recommendedObjective required (max 2000)");
        DateOnly startDate = default;
        if (!DateOnly.TryParseExact(Str(root, "virtualStartDate") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
            problems.Add("virtualStartDate must be yyyy-MM-dd");

        var merchants = new List<ScenarioMerchant>();
        if (root["merchants"] is JsonArray merchantArray && merchantArray.Count is >= 1 and <= 20)
        {
            foreach (var node in merchantArray)
            {
                if (node is not JsonObject merchant) { problems.Add("merchant must be an object"); continue; }
                foreach (var key in merchant.Select(p => p.Key).Where(k => !MerchantFields.Contains(k))) problems.Add($"unknown merchant field {key}");
                var key2 = Str(merchant, "key");
                var label = Str(merchant, "label");
                if (key2 is null || !KeyPattern.IsMatch(key2)) problems.Add("merchant key invalid");
                if (label is null || label.Length > 100 || !label.EndsWith("(synthetic)", StringComparison.Ordinal))
                    problems.Add($"merchant {key2} label must end with (synthetic) (max 100)");
                if (key2 is not null && label is not null) merchants.Add(new ScenarioMerchant(key2, label));
            }
            if (merchants.Select(m => m.Key).Distinct().Count() != merchants.Count) problems.Add("duplicate merchant key");
        }
        else problems.Add("merchants must be an array of 1 to 20 items");

        var events = new List<ScenarioEventDefinition>();
        if (root["events"] is JsonArray eventArray && eventArray.Count <= 200)
        {
            foreach (var node in eventArray)
            {
                if (node is not JsonObject evt) { problems.Add("event must be an object"); continue; }
                var key = Str(evt, "key");
                var day = Int(evt, "day");
                var kind = Str(evt, "kind");
                if (key is null || !KeyPattern.IsMatch(key)) problems.Add("event key invalid");
                if (day is null or < 0 or > 365) problems.Add($"event {key} day must be 0..365");
                if (kind == "message")
                {
                    foreach (var field in evt.Select(p => p.Key).Where(k => !MessageFields.Contains(k))) problems.Add($"event {key}: unknown field {field}");
                    var from = Str(evt, "from");
                    var text = Str(evt, "text");
                    if (from is null || from.Length > 100 || !from.EndsWith("(synthetic)", StringComparison.Ordinal))
                        problems.Add($"event {key}: from must end with (synthetic) (max 100)");
                    if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) problems.Add($"event {key}: text required (max 2000)");
                    events.Add(new ScenarioEventDefinition(key ?? "", day ?? 0, kind, from, text, null, null, null, null, null, null));
                }
                else if (kind == "obligation")
                {
                    foreach (var field in evt.Select(p => p.Key).Where(k => !ObligationFields.Contains(k))) problems.Add($"event {key}: unknown field {field}");
                    var merchant = Str(evt, "merchant");
                    if (merchant is null || merchants.All(m => m.Key != merchant)) problems.Add($"event {key}: unknown merchant");
                    long? fixedAmount = null;
                    (long, long, long)? range = null;
                    switch (evt["amountSen"])
                    {
                        case JsonValue value when TryLong(value, out var amount) && amount > 0:
                            fixedAmount = amount;
                            break;
                        case JsonObject rangeObject when rangeObject.Count == 3
                            && Long(rangeObject, "min") is long min && min > 0
                            && Long(rangeObject, "max") is long max && max >= min
                            && Long(rangeObject, "step") is long step && step > 0 && (max - min) % step == 0:
                            range = (min, max, step);
                            break;
                        default:
                            problems.Add($"event {key}: amountSen must be a positive integer or a valid range");
                            break;
                    }
                    var dueDay = Int(evt, "dueDay");
                    if (dueDay is null || dueDay < (day ?? 0) || dueDay > 365) problems.Add($"event {key}: dueDay must be day..365");
                    var reference = Str(evt, "invoiceReference");
                    try { InvoiceReferences.Validate(reference); }
                    catch (LedgerException) { problems.Add($"event {key}: invalid invoiceReference"); }
                    var description = Str(evt, "description");
                    if (string.IsNullOrWhiteSpace(description) || description.Length > 200) problems.Add($"event {key}: description required (max 200)");
                    events.Add(new ScenarioEventDefinition(key ?? "", day ?? 0, kind, null, null, merchant, fixedAmount, range,
                        dueDay, reference, description));
                }
                else problems.Add($"event {key}: kind must be message or obligation");
            }

            if (events.Select(e => e.Key).Distinct().Count() != events.Count) problems.Add("duplicate event key");
            foreach (var message in events.Where(e => e.Kind == "message" && e.Text is not null))
            {
                foreach (Match match in PlaceholderPattern.Matches(message.Text!))
                {
                    if (events.All(e => e.Kind != "obligation" || e.Key != match.Groups[1].Value))
                        problems.Add($"event {message.Key}: placeholder references unknown obligation {match.Groups[1].Value}");
                }
            }
            // Events sharing an invoice reference model a duplicate charge; they must describe the same obligation.
            foreach (var group in events.Where(e => e.Kind == "obligation").GroupBy(e => e.InvoiceReference))
            {
                if (group.Count() > 1 && (group.Select(e => e.Merchant).Distinct().Count() > 1
                        || group.Any(e => e.FixedAmountSen is null) || group.Select(e => e.FixedAmountSen).Distinct().Count() > 1))
                    problems.Add($"invoice {group.Key}: shared references need the same merchant and fixed amount");
            }
        }
        else problems.Add("events must be an array of at most 200 items");

        if (problems.Count > 0) throw new ScenarioValidationException(fileName, problems);
        return new ScenarioDefinition(id!, version!.Value, title!, objective!, startDate, merchants, events,
            Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    // Draws ranged values in file order and resolves message placeholders. Deterministic for (scenario, seed).
    public static IReadOnlyList<MaterializedScenarioEvent> Materialize(ScenarioDefinition scenario, long seed)
    {
        var random = Xoshiro256StarStar.ForScenario(scenario.Id, scenario.Version, seed);
        var amounts = new Dictionary<string, long>();
        foreach (var evt in scenario.Events.Where(e => e.Kind == "obligation"))
            amounts[evt.Key] = evt.AmountRange is { } range ? random.NextInRange(range.Min, range.Max, range.Step) : evt.FixedAmountSen!.Value;

        return scenario.Events.Select(evt => new MaterializedScenarioEvent(evt.Key, evt.Day, evt.Kind, evt.Kind == "message"
            ? new JsonObject
            {
                ["from"] = evt.From,
                ["text"] = PlaceholderPattern.Replace(evt.Text!, match => FormatRinggit(amounts[match.Groups[1].Value]))
            }
            : new JsonObject
            {
                ["merchant"] = evt.Merchant,
                ["amountSen"] = amounts[evt.Key],
                ["dueDay"] = evt.DueDay,
                ["invoiceReference"] = evt.InvoiceReference,
                ["description"] = evt.Description
            })).ToArray();
    }

    public static string ScheduleHash(IReadOnlyList<MaterializedScenarioEvent> schedule)
    {
        var array = new JsonArray(schedule.Select(e => (JsonNode)new JsonObject
        {
            ["key"] = e.Key, ["day"] = e.Day, ["kind"] = e.Kind, ["payload"] = e.Payload.DeepClone()
        }).ToArray());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(array.ToJsonString())));
    }

    public static string FormatRinggit(long sen) =>
        "RM" + (sen / 100).ToString("N0", CultureInfo.InvariantCulture) + "." + (sen % 100).ToString("00", CultureInfo.InvariantCulture);

    private static string? Str(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject source, string key) =>
        source[key] is JsonValue value && TryLong(value, out var number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    private static long? Long(JsonObject source, string key) =>
        source[key] is JsonValue value && TryLong(value, out var number) ? number : null;

    private static bool TryLong(JsonValue value, out long number)
    {
        number = 0;
        return value.GetValueKind() == System.Text.Json.JsonValueKind.Number && value.TryGetValue(out number);
    }
}

// Virtual clock state and the agent-visible environment (ADR-0010).
public sealed record ClockResult(int Day, DateOnly VirtualDate, int DaysRemaining, bool DurationReached, IReadOnlyList<string> DeliveredEventKeys);

public sealed record EnvironmentMessage(string MessageId, int Day, string From, string Text);

public sealed record EnvironmentView(DateOnly VirtualDate, int Day, int DaysRemaining, IReadOnlyList<EnvironmentMessage> Messages);
