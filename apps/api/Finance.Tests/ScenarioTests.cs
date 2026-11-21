using System.Text;
using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0010: scenario files, seeded materialization, and the virtual clock.
public sealed class ScenarioTests
{
    private const string Source = "internal_api";

    // Test vector computed with an independent Python implementation of xoshiro256** (ADR-0010).
    [Fact]
    public void Prng_matches_published_test_vector()
    {
        var random = Xoshiro256StarStar.ForScenario("S02", 1, 42);
        Assert.Equal([17067664432659434232UL, 18040955551344981867UL, 8957787423657356057UL],
            new[] { random.Next(), random.Next(), random.Next() });
    }

    [Theory]
    [InlineData(42, 313_900, "RM3,139.00")]
    [InlineData(43, 321_100, "RM3,211.00")]
    [InlineData(0, 321_000, "RM3,210.00")]
    public void S02_repair_amount_is_seeded(long seed, long expectedSen, string expectedText)
    {
        var schedule = ScenarioCatalog.Materialize(ScenarioCatalog.Latest("S02")!, seed);
        Assert.Equal(expectedSen, schedule.Single(e => e.Key == "repair").Payload["amountSen"]!.GetValue<long>());
        Assert.Contains(expectedText, schedule.Single(e => e.Key == "repair_note").Payload["text"]!.GetValue<string>());
    }

    [Fact]
    public void Same_seed_same_schedule_and_other_seeds_change_only_ranged_values()
    {
        var scenario = ScenarioCatalog.Latest("S02")!;
        Assert.Equal(ScenarioCatalog.ScheduleHash(ScenarioCatalog.Materialize(scenario, 7)),
            ScenarioCatalog.ScheduleHash(ScenarioCatalog.Materialize(scenario, 7)));

        var first = ScenarioCatalog.Materialize(scenario, 1);
        var second = ScenarioCatalog.Materialize(scenario, 2);
        foreach (var (a, b) in first.Zip(second).Where(pair => pair.First.Key is not ("repair" or "repair_note")))
            Assert.True(JsonNode.DeepEquals(a.Payload, b.Payload), a.Key);
    }

    [Fact]
    public void Shipped_scenarios_load_and_S01_S03_share_events()
    {
        Assert.Equal(["S01/v1", "S02/v1", "S03/v1"], ScenarioCatalog.All.Values.Select(s => s.VersionLabel).Order());
        var s01 = ScenarioCatalog.Materialize(ScenarioCatalog.Latest("S01")!, 5);
        var s03 = ScenarioCatalog.Materialize(ScenarioCatalog.Latest("S03")!, 5);
        Assert.Equal(ScenarioCatalog.ScheduleHash(s01), ScenarioCatalog.ScheduleHash(s03));
        Assert.NotEqual(ScenarioCatalog.Latest("S01")!.RecommendedObjective, ScenarioCatalog.Latest("S03")!.RecommendedObjective);
    }

    public static TheoryData<string, string> InvalidFiles() => new()
    {
        { "unknown_field", "unknown field extra" },
        { "unknown_merchant", "unknown merchant" },
        { "bad_range", "valid range" },
        { "duplicate_event", "duplicate event key" },
        { "real_label", "(synthetic)" },
        { "bad_placeholder", "unknown obligation" },
        { "shared_invoice_mismatch", "shared references" },
        { "file_name", "file name" },
        { "due_before_day", "dueDay" },
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_scenario_files_are_rejected(string name, string expectedProblem)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ScenarioCatalog.FindDirectory(), "S02.v1.json")))!.AsObject();
        var events = root["events"]!.AsArray();
        var fileName = "S02.v1.json";
        switch (name)
        {
            case "unknown_field": root["extra"] = 1; break;
            case "unknown_merchant": events[1]!["merchant"] = "nobody"; break;
            case "bad_range": events[4]!["amountSen"] = new JsonObject { ["min"] = 10, ["max"] = 15, ["step"] = 2 }; break;
            case "duplicate_event": events[2]!["key"] = "rent"; break;
            case "real_label": root["merchants"]![0]!["label"] = "Real Bank Berhad"; break;
            case "bad_placeholder": events[5]!["text"] = "Pay {nothing.amount}"; break;
            case "shared_invoice_mismatch": events[2]!["invoiceReference"] = "RENT-01"; break;
            case "file_name": fileName = "S02.v2.json"; break;
            case "due_before_day": events[4]!["dueDay"] = 3; break;
        }

        var exception = Assert.Throws<ScenarioValidationException>(() =>
            ScenarioCatalog.Parse(fileName, Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Contains(exception.Problems, problem => problem.Contains(expectedProblem));
    }

    private static Guid ActiveRun(IFinanceLedger ledger, string scenarioId = "S01", long seed = 42, int durationDays = 30)
    {
        var config = new JsonObject
        {
            ["objective"] = "Manage the provided virtual funds responsibly.",
            ["scenarioId"] = scenarioId,
            ["seed"] = seed,
            ["startingBalanceSen"] = 1_000_000,
            ["transactionLimitSen"] = 400_000,
            ["durationDays"] = durationDays,
            ["maxActions"] = 100,
            ["maxModelTokens"] = 0,
            ["maxWallTimeSeconds"] = 600,
            ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        };
        var run = ledger.CreateManagedRun(config, null);
        ledger.TransitionRun(run.RunId, RunStatus.Queued, null, Source);
        ledger.TransitionRun(run.RunId, RunStatus.Initializing, null, Source);
        ledger.ActivateRun(run.RunId, Source);
        return run.RunId;
    }

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["activation_materializes_and_delivers_day_zero"] = ledger =>
        {
            var runId = ActiveRun(ledger);
            var events = ledger.GetEvents(runId, 0, 500);

            var merchants = events.Where(e => e.EventType == "ACCOUNT_CREATED" && e.Payload["scenarioKey"] is not null).ToArray();
            Assert.Equal(["landlord", "utility", "grocer"], merchants.Select(e => e.Payload["scenarioKey"]!.GetValue<string>()));
            Assert.All(merchants, e => Assert.EndsWith("(synthetic)", e.Payload["label"]!.GetValue<string>()));

            var materialized = events.Single(e => e.EventType == "SCENARIO_MATERIALIZED");
            Assert.Equal(4, materialized.Payload["eventCount"]!.GetValue<int>());
            Assert.Equal(ScenarioCatalog.ScheduleHash(ScenarioCatalog.Materialize(ScenarioCatalog.Latest("S01")!, 42)),
                materialized.Payload["scheduleHash"]!.GetValue<string>());
            Assert.Equal(Xoshiro256StarStar.Algorithm, materialized.Payload["prng"]!.GetValue<string>());

            var environment = ledger.GetEnvironment(runId);
            Assert.Equal(0, environment.Day);
            Assert.Equal(new DateOnly(2030, 1, 1), environment.VirtualDate);
            Assert.Equal(30, environment.DaysRemaining);
            Assert.Equal("welcome", Assert.Single(environment.Messages).MessageId);
            Assert.DoesNotContain(events, e => e.EventType == "OBLIGATION_CREATED");
            Assert.All(events.Skip(3), e => Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), e.VirtualTime));

            var order = events.Select(e => e.EventType).ToArray();
            Assert.True(Array.IndexOf(order, "SCENARIO_MATERIALIZED") < Array.LastIndexOf(order, "SCENARIO_EVENT_DELIVERED"));
            Assert.Equal("RUN_STATUS_CHANGED", order[^1]);
        },
        ["wait_delivers_due_events_once_in_order"] = ledger =>
        {
            var runId = ActiveRun(ledger);
            var first = ledger.AdvanceClock(runId, 1, Source);
            Assert.Equal(1, first.Day);
            Assert.Equal(["rent"], first.DeliveredEventKeys);

            var rentEvent = ledger.GetEvents(runId, 0, 500).Single(e => e.EventType == "OBLIGATION_CREATED");
            var rent = ledger.GetObligation(runId, rentEvent.ObligationId!.Value);
            Assert.Equal((150_000L, "RENT-01", 5, "Monthly rent", false),
                (rent.AmountSen, rent.InvoiceReference, rent.DueDay!.Value, rent.Label!, rent.Paid));

            var second = ledger.AdvanceClock(runId, 30, Source);
            Assert.Equal((30, true, 0), (second.Day, second.DurationReached, second.DaysRemaining));
            Assert.Equal(["utilities", "groceries"], second.DeliveredEventKeys);

            var before = ledger.GetEvents(runId, 0, 500).Count;
            var third = ledger.AdvanceClock(runId, 5, Source);
            Assert.Equal((30, true), (third.Day, third.DurationReached));
            Assert.Empty(third.DeliveredEventKeys);
            Assert.Equal(before, ledger.GetEvents(runId, 0, 500).Count);

            var events = ledger.GetEvents(runId, 0, 500);
            Assert.Equal(4, events.Count(e => e.EventType == "SCENARIO_EVENT_DELIVERED"));
            Assert.Equal(3, events.Count(e => e.EventType == "OBLIGATION_CREATED"));
            var clock = events.Where(e => e.EventType == "CLOCK_ADVANCED").ToArray();
            Assert.Equal([(0, 1), (1, 30)], clock.Select(e => (e.Payload["fromDay"]!.GetValue<int>(), e.Payload["toDay"]!.GetValue<int>())));
            Assert.Equal(new DateTimeOffset(2030, 1, 31, 0, 0, 0, TimeSpan.Zero), clock[1].VirtualTime);
        },
        ["seeded_obligation_is_payable"] = ledger =>
        {
            var runId = ActiveRun(ledger, "S02", seed: 42);
            ledger.AdvanceClock(runId, 10, Source);
            var repairEvent = ledger.GetEvents(runId, 0, 500)
                .Single(e => e.EventType == "OBLIGATION_CREATED" && e.Payload["scenarioEventKey"]!.GetValue<string>() == "repair");
            var repair = ledger.GetObligation(runId, repairEvent.ObligationId!.Value);
            Assert.Equal(313_900, repair.AmountSen);
            Assert.Contains(ledger.GetEnvironment(runId).Messages, m => m.MessageId == "repair_note" && m.Text.Contains("RM3,139.00"));

            var cash = ledger.GetEvents(runId, 0, 500).First(e => e.EventType == "ACCOUNT_CREATED" && e.Payload["kind"]!.GetValue<string>() == "agent_cash")
                .Payload["accountId"]!.GetValue<Guid>();
            ledger.PayObligation(runId, "pay-repair", cash, repair.ObligationId);
            Assert.Equal(1_000_000 - 313_900, ledger.GetBalance(runId, cash));
        },
        ["short_duration_never_delivers_later_events"] = ledger =>
        {
            var runId = ActiveRun(ledger, durationDays: 5);
            var result = ledger.AdvanceClock(runId, 30, Source);
            Assert.Equal((5, true), (result.Day, result.DurationReached));
            Assert.Equal(["rent", "utilities"], result.DeliveredEventKeys);
        },
        ["clock_rejections"] = ledger =>
        {
            var runId = ActiveRun(ledger);
            Assert.Equal(LedgerReasonCode.InvalidAmount, Assert.ThrowsAny<LedgerException>(() => ledger.AdvanceClock(runId, 0, Source)).ReasonCode);
            ledger.TransitionRun(runId, RunStatus.Cancelled, "stop", Source);
            Assert.Equal(LedgerReasonCode.RunNotActive, Assert.ThrowsAny<LedgerException>(() => ledger.AdvanceClock(runId, 1, Source)).ReasonCode);
            Assert.Equal(0, ledger.GetEnvironment(runId).Day);

            var legacy = Guid.NewGuid();
            ledger.OpenRun(legacy, "open", 100, 100);
            Assert.Equal(LedgerReasonCode.RunNotActive, Assert.ThrowsAny<LedgerException>(() => ledger.AdvanceClock(legacy, 1, Source)).ReasonCode);
            Assert.Equal(LedgerReasonCode.RunNotActive, Assert.ThrowsAny<LedgerException>(() => ledger.GetEnvironment(legacy)).ReasonCode);

            var draft = ledger.CreateManagedRun(new JsonObject
            {
                ["objective"] = "x", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100, ["transactionLimitSen"] = 100,
                ["durationDays"] = 5, ["maxActions"] = 5, ["maxModelTokens"] = 0, ["maxWallTimeSeconds"] = 60, ["memoryMode"] = "none",
                ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
            }, null);
            Assert.Equal(LedgerReasonCode.RunNotActive, Assert.ThrowsAny<LedgerException>(() => ledger.GetEnvironment(draft.RunId)).ReasonCode);
        },
    };

    public static TheoryData<string> ScenarioNames() => new(Scenarios.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void InMemory(string scenario) => Scenarios[scenario](new InMemoryLedger());

    [PostgresTheory]
    [MemberData(nameof(ScenarioNames))]
    public void Postgres(string scenario)
    {
        using var dataSource = OwnerDataSource();
        Scenarios[scenario](new PostgresLedger(dataSource));
    }

    [PostgresFact]
    public void Both_ledgers_record_the_same_scenario_events()
    {
        using var dataSource = OwnerDataSource();
        var ledgers = new IFinanceLedger[] { new InMemoryLedger(), new PostgresLedger(dataSource) };
        var traces = ledgers.Select(ledger =>
        {
            var runId = ActiveRun(ledger, "S02", seed: 9);
            ledger.AdvanceClock(runId, 3, Source);
            ledger.AdvanceClock(runId, 20, Source);
            return ledger.GetEvents(runId, 0, 500)
                .Select(e => $"{e.EventType}|{e.Operation}|{e.Payload["eventKey"]}|{e.Payload["amountSen"]}|{e.Payload["scheduleHash"]}|{e.VirtualTime:yyyy-MM-dd}")
                .ToArray();
        }).ToArray();
        Assert.Equal(traces[0], traces[1]);
    }

    [PostgresFact]
    public void Postgres_database_protects_clock_and_schedule()
    {
        using var dataSource = OwnerDataSource();
        var runId = ActiveRun(new PostgresLedger(dataSource));
        new PostgresLedger(dataSource).AdvanceClock(runId, 3, Source);

        AssertSqlFails(dataSource, "UPDATE experiment_runs SET virtual_day = 1 WHERE id = @id", runId, "55000");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET virtual_start_date = '2031-01-01' WHERE id = @id", runId, "55000");
        AssertSqlFails(dataSource, "UPDATE scenario_events SET payload = '{}' WHERE run_id = @id", runId, "55000");
        AssertSqlFails(dataSource, "UPDATE scenario_events SET delivered_at_day = 3 WHERE run_id = @id AND delivered_at_day IS NOT NULL", runId, "55000");
        AssertSqlFails(dataSource, "DELETE FROM scenario_events WHERE run_id = @id", runId, "55000");

        var apiConnectionString = Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!;
        using var apiSource = NpgsqlDataSource.Create(apiConnectionString);
        AssertSqlFails(apiSource, "UPDATE scenario_events SET payload = '{}' WHERE run_id = @id", runId, "42501");
        AssertSqlFails(apiSource, "DELETE FROM scenario_events WHERE run_id = @id", runId, "42501");
    }

    [PostgresFact]
    public void Postgres_restricted_role_runs_scenarios()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var ledger = new PostgresLedger(dataSource);
        var runId = ActiveRun(ledger, "S02");
        var result = ledger.AdvanceClock(runId, 30, Source);
        Assert.Equal(["rent", "utilities", "groceries", "repair", "repair_note"], result.DeliveredEventKeys);
        Assert.Equal(2, ledger.GetEnvironment(runId).Messages.Count);
    }

    private static void AssertSqlFails(NpgsqlDataSource dataSource, string sql, Guid id, string sqlState)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(sqlState, Assert.Throws<PostgresException>(() => command.ExecuteNonQuery()).SqlState);
    }

    private static NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
}
