using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0013 A2 (items 6, 8, 9, 10, 11): what a researcher reads is what happened.
[Collection(QueuedRunTests.Name)]
public sealed class IntegrityTests
{
    private static Guid RunningRun(IFinanceLedger ledger)
    {
        var run = ledger.CreateManagedRun(new JsonObject
        {
            ["objective"] = "Manage funds.", ["scenarioId"] = "S02", ["seed"] = 42, ["startingBalanceSen"] = 1_000_000,
            ["transactionLimitSen"] = 400_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = 0,
            ["maxWallTimeSeconds"] = 3_600, ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        }, null).RunId;
        ledger.TransitionRun(run, RunStatus.Queued, null, "internal_api");
        ledger.TransitionRun(run, RunStatus.Initializing, null, FinanceEvents.WorkerSource);
        ledger.ActivateRun(run, FinanceEvents.WorkerSource);
        return run;
    }

    // What a crash between the clock change and the tool completion leaves behind.
    private static ClockResult InterruptedWait(IFinanceLedger ledger, Guid run, string id, int days)
    {
        var arguments = new JsonObject { ["days"] = days };
        ledger.BeginToolCall(run, id, "wait", (JsonObject)arguments.DeepClone(), ToolGateway.RequestHash("wait", arguments));
        using (FinanceCallContext.Begin(FinanceEvents.AgentSource, id))
            return ledger.AdvanceClock(run, days, FinanceEvents.AgentSource);
    }

    private static JsonObject Wait(IFinanceLedger ledger, Guid run, string id, int days) =>
        new ToolGateway(ledger, TimeProvider.System).Execute(run, id, "wait", new JsonObject { ["days"] = days });

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["resumed_wait_moves_the_clock_once"] = ledger =>
        {
            var run = RunningRun(ledger);
            var first = InterruptedWait(ledger, run, "turn-0001", 3);
            var resumed = Wait(ledger, run, "turn-0001", 3)["data"]!;
            Assert.Equal(3, ledger.GetEnvironment(run).Day);
            Assert.Equal((first.Day, first.DeliveredEventKeys.Count), ((int)resumed["day"]!, (int)resumed["delivered_count"]!));
            Assert.Single(ledger.GetEvents(run, 0, 500), e => e.EventType == "CLOCK_ADVANCED");
        },
        ["resumed_wait_at_the_end_reports_the_end"] = ledger =>
        {
            var run = RunningRun(ledger);
            InterruptedWait(ledger, run, "turn-0001", 30);
            var resumed = Wait(ledger, run, "turn-0001", 30)["data"]!;
            Assert.Equal((30, true), ((int)resumed["day"]!, (bool)resumed["duration_reached"]!));
        },
        ["concurrent_retries_of_one_wait_move_the_clock_once"] = ledger =>
        {
            var run = RunningRun(ledger);
            Task.WaitAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                try { Wait(ledger, run, "turn-0001", 3); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { }
            })).ToArray());
            Assert.Equal(3, ledger.GetEnvironment(run).Day);
        },
        ["separate_waits_each_move_the_clock"] = ledger =>
        {
            var run = RunningRun(ledger);
            Wait(ledger, run, "turn-0001", 3);
            Wait(ledger, run, "turn-0002", 3);
            Assert.Equal(6, ledger.GetEnvironment(run).Day);
        },
        ["internal_failure_is_an_error_not_a_denial"] = ledger =>
        {
            var run = RunningRun(ledger);
            var cash = ledger.GetAgentView(run).CashAccountId;
            var before = ledger.GetEvents(run, 0, 500).Count;
            var request = new JournalRequest(run, "broken", JournalKind.Transfer,
                "unbalanced", [new PostingRequest(cash, -100), new PostingRequest(Guid.NewGuid(), 99)]);
            // The generic Commit is not on the interface.
            Func<JournalRequest, CommitResult> commit = ledger is PostgresLedger postgres ? postgres.Commit : ((InMemoryLedger)ledger).Commit;
            Assert.ThrowsAny<LedgerException>(() => commit(request));
            var recorded = Assert.Single(ledger.GetEvents(run, before, 500));
            Assert.Equal(("FINANCE_ERROR", "internal_error", "LEDGER_INVARIANT_VIOLATION"),
                (recorded.EventType, recorded.Outcome, recorded.ReasonCode));
        },
        ["agent_text_is_cut_between_code_points"] = ledger =>
        {
            var run = RunningRun(ledger);
            var text = new string('a', ToolArgumentRedaction.MaxStringLength - 1) + "😀 and more";
            new ToolGateway(ledger, TimeProvider.System).Execute(run, "turn-0001", "submit_plan", new JsonObject { ["text"] = text });
            var stored = (string)ledger.GetEvents(run, 0, 500).Single(e => e.EventType == "TOOL_CALL_PROPOSED").Payload["arguments"]!["text"]!;
            Assert.Equal(new string('a', ToolArgumentRedaction.MaxStringLength - 1) + ToolArgumentRedaction.TruncatedMarker, stored);
        },
        ["status_reason_is_cut_between_code_points"] = ledger =>
        {
            var run = RunningRun(ledger);
            ledger.TransitionRun(run, RunStatus.Cancelled, new string('r', 499) + "😀 and more", "internal_api");
            Assert.Equal(new string('r', 499), ledger.GetRun(run).StatusReason);
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
        // The restricted role, so the grants are tested too.
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        Scenarios[scenario](new PostgresLedger(dataSource));
    }

    [Fact]
    public void Internal_error_response_says_the_outcome_is_unknown()
    {
        var (status, body) = LedgerHttpResponses.ForException(new TimeoutException());
        Assert.Equal(500, status);
        Assert.Contains("outcome is unknown", body.Message);
        Assert.Contains("same idempotency key", body.Message);
        Assert.Null((object?)body.Committed);
    }

    [Fact]
    public void Failed_read_says_nothing_changed()
    {
        var (status, body) = LedgerHttpResponses.ForReadException(new TimeoutException());
        Assert.Equal(500, status);
        Assert.Contains("read", body.Message);
        Assert.Equal(false, (object?)body.Committed);
    }

    // One altered value for every stored column. A column added later fails the test until it is covered.
    private static readonly Dictionary<string, string> Alterations = new()
    {
        ["id"] = "gen_random_uuid()", ["run_id"] = "gen_random_uuid()", ["sequence"] = "sequence + 100000",
        ["event_type"] = "'FINANCE_ERROR'", ["event_version"] = "event_version + 1",
        ["occurred_at"] = "occurred_at + interval '1 microsecond'", ["virtual_time"] = "virtual_time + interval '1 day'",
        ["source"] = "'scenario_engine'", ["trace_id"] = "'turn-9999'", ["operation"] = "'altered'",
        ["outcome"] = "'conflict'", ["reason_code"] = "'ALTERED'", ["idempotency_key"] = "'altered'",
        ["journal_id"] = "gen_random_uuid()", ["obligation_id"] = "gen_random_uuid()",
        ["payload"] = "'{\"altered\":true}'::jsonb", ["prev_hash"] = "'\\x01'::bytea", ["event_hash"] = "'\\x02'::bytea",
        ["hash_version"] = "1",
    };

    [PostgresFact]
    public void Altering_any_stored_column_breaks_verification()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        var ledger = new PostgresLedger(dataSource);
        var run = RunningRun(ledger);
        new ToolGateway(ledger, TimeProvider.System).Execute(run, "turn-0001", "move_to_savings",
            new JsonObject { ["amount_sen"] = 400_001, ["idempotency_key"] = "too-much" });

        using var connection = dataSource.OpenConnection();
        var columns = new List<string>();
        using (var list = new NpgsqlCommand(
            "SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'run_events'", connection))
        using (var reader = list.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));

        var undetected = new List<string>();
        foreach (var column in columns)
        {
            Assert.True(Alterations.ContainsKey(column), $"{column} is stored but has no tamper test; add it to the hash.");
            using var transaction = connection.BeginTransaction();
            Run(connection, transaction, "SET LOCAL session_replication_role = replica", run); // owner only; rolled back
            Run(connection, transaction, $"UPDATE run_events SET {column} = {Alterations[column]} WHERE run_id = @run AND event_type = 'POLICY_DENIED'", run);
            if (Run(connection, transaction, "SELECT finmind_verify_run_events(@run)", run) is not long) undetected.Add(column);
            transaction.Rollback();
        }
        Assert.Empty(undetected);
    }

    [PostgresFact]
    public void Version_1_events_still_verify_and_cannot_follow_version_2()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        var run = RunningRun(new PostgresLedger(dataSource));
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        const string insertV1 =
            "WITH link AS (SELECT (SELECT event_hash FROM run_events WHERE run_id = @run ORDER BY sequence DESC LIMIT 1) AS prev, " +
            "(SELECT coalesce(max(sequence), 0) + 1 FROM run_events WHERE run_id = @run) AS seq) " +
            "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, payload, prev_hash, event_hash, hash_version) " +
            "SELECT gen_random_uuid(), @run, seq, 'FINANCE_ERROR', 1, now(), 'internal_api', '{}', prev, " +
            "finmind_run_event_hash(prev, @run, seq, 'FINANCE_ERROR', 1, now(), 'internal_api', '{}'::jsonb), 1 FROM link";

        Run(connection, transaction, "SET LOCAL session_replication_role = replica", run);
        Run(connection, transaction, "DELETE FROM run_events WHERE run_id = @run", run);
        Run(connection, transaction, insertV1, run);
        Run(connection, transaction, "SET LOCAL session_replication_role = origin", run);
        Run(connection, transaction,
            "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, payload, event_hash, hash_version) " +
            "VALUES (gen_random_uuid(), @run, 0, 'FINANCE_ERROR', 1, now(), 'internal_api', '{}', '\\x00', 1)", run);
        Assert.Equal((short)2, Run(connection, transaction, "SELECT hash_version FROM run_events WHERE run_id = @run AND sequence = 2", run));
        Assert.Equal(DBNull.Value, Run(connection, transaction, "SELECT finmind_verify_run_events(@run)", run));

        Run(connection, transaction, "SET LOCAL session_replication_role = replica", run);
        Run(connection, transaction, insertV1, run);
        Assert.Equal(3L, Run(connection, transaction, "SELECT finmind_verify_run_events(@run)", run));
        transaction.Rollback();
    }

    private static object? Run(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid run)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("@run")) command.Parameters.AddWithValue("run", run);
        return command.ExecuteScalar();
    }
}
