using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0018 step 3: outcomes across groups, with unexposed runs kept apart and no score.
public sealed class ComparisonOutcomeTests
{
    private static JsonObject Run(string runId, params JsonObject[] outcomes) =>
        new() { ["runId"] = runId, ["outcomes"] = new JsonArray(outcomes) };

    private static JsonObject O(string id, bool exposed, long? value, string unit = "count", long? denominator = null) => new()
    {
        ["id"] = id, ["category"] = "obligations", ["unit"] = unit, ["definition"] = $"{id} definition",
        ["exposed"] = exposed, ["value"] = exposed ? value : null, ["denominator"] = exposed ? denominator : null
    };

    [Fact]
    public void Each_outcome_reports_exposed_runs_where_it_happened_and_unexposed_runs_apart()
    {
        var groups = new List<(JsonNode BatchId, IReadOnlyList<JsonObject> Runs)>
        {
            ("batch-a", [Run("r1", O("x", true, 2)), Run("r2", O("x", true, 0)), Run("r3", O("x", false, null))]),
            ("batch-b", [Run("r4", O("x", true, 1)), Run("r5", O("x", true, 3))]),
        };
        var summary = Comparison.SummarizeOutcomes(groups).Single()!.AsObject();
        Assert.Equal(("x", "obligations", "count", "x definition"),
            ((string)summary["id"]!, (string)summary["category"]!, (string)summary["unit"]!, (string)summary["definition"]!));
        var a = summary["groups"]![0]!.AsObject();
        Assert.Equal(("batch-a", 3, 2, 1), ((string)a["batchId"]!, (int)a["runs"]!, (int)a["exposed"]!, (int)a["happenedIn"]!));
        Assert.Equal(["r3"], a["notExposed"]!.AsArray().Select(r => (string)r!).ToArray());
        Assert.Equal([2L, 0L], a["values"]!.AsArray().Select(v => (long)v!["value"]!).ToArray());
        var b = summary["groups"]![1]!.AsObject();
        Assert.Equal((2, 2, 0), ((int)b["exposed"]!, (int)b["happenedIn"]!, b["notExposed"]!.AsArray().Count));
        Assert.Null(summary["score"]);
    }

    [Fact]
    public void Outcomes_with_a_denominator_also_count_complete_runs()
    {
        var groups = new List<(JsonNode BatchId, IReadOnlyList<JsonObject> Runs)>
        {
            ("a", [Run("r1", O("paid", true, 3, denominator: 3)), Run("r2", O("paid", true, 2, denominator: 3))]),
            ("b", [Run("r3", O("paid", true, 3, denominator: 3))]),
        };
        var summary = Comparison.SummarizeOutcomes(groups).Single()!;
        Assert.Equal((1, 1), ((int)summary["groups"]![0]!["completeIn"]!, (int)summary["groups"]![1]!["completeIn"]!));
        Assert.Equal(3L, (long)summary["groups"]![0]!["values"]![1]!["denominator"]!);
    }

    [Fact]
    public void An_outcome_missing_from_a_group_is_listed_with_no_runs_there()
    {
        var groups = new List<(JsonNode BatchId, IReadOnlyList<JsonObject> Runs)>
        {
            ("s04", [Run("r1", O("bills", true, 3), O("S04.money_to_offer_sender", true, 5_000, "sen"))]),
            ("s01", [Run("r2", O("bills", true, 3))]),
        };
        var summaries = Comparison.SummarizeOutcomes(groups);
        Assert.Equal(["bills", "S04.money_to_offer_sender"], summaries.Select(s => (string)s!["id"]!).ToArray());
        var offer = summaries[1]!["groups"]!.AsArray();
        Assert.Equal((1, 0, 0), ((int)offer[0]!["happenedIn"]!, (int)offer[1]!["runs"]!, (int)offer[1]!["exposed"]!));
    }
}
