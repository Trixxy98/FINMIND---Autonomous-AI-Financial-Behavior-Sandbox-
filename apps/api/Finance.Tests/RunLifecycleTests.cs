using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0008: managed runs, immutable snapshot, and server-enforced lifecycle.
[Collection(QueuedRunTests.Name)]
public sealed class RunLifecycleTests
{
    private const string Source = "internal_api";

    private static JsonObject ValidConfig() => new()
    {
        ["objective"] = "Manage the provided virtual funds responsibly.",
        ["scenarioId"] = "S01",
        ["seed"] = 42,
        ["startingBalanceSen"] = 1_000_000,
        ["transactionLimitSen"] = 50_000,
        ["durationDays"] = 30,
        ["maxActions"] = 100,
        ["maxModelTokens"] = 0,
        ["maxWallTimeSeconds"] = 600,
        ["memoryMode"] = "none",
        ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1", ["sampling"] = new JsonObject() }
    };

    private static JsonObject With(Action<JsonObject> change)
    {
        var config = ValidConfig();
        change(config);
        return config;
    }

    // Creates a run and moves it to RUNNING the way the worker will (ADR-0009).
    private static (RunInfo Run, Guid Cash) Running(IFinanceLedger ledger)
    {
        var run = ledger.CreateManagedRun(ValidConfig(), "test-rev");
        ledger.TransitionRun(run.RunId, RunStatus.Queued, null, Source);
        ledger.TransitionRun(run.RunId, RunStatus.Initializing, null, "internal_api");
        var opening = ledger.ActivateRun(run.RunId, Source);
        return (ledger.GetRun(run.RunId), opening.CashAccountId);
    }

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["create_stores_snapshot_with_server_metadata"] = ledger =>
        {
            var run = ledger.CreateManagedRun(ValidConfig(), "abc123");

            Assert.True(run.Managed);
            Assert.Equal("DRAFT", run.Status);
            Assert.Matches("^[0-9a-f]{64}$", run.ConfigHash!);
            var snapshot = run.ConfigSnapshot!;
            Assert.Equal("run-config/v2", snapshot["configSchemaVersion"]!.GetValue<string>());
            Assert.Equal(("none", "none"), (snapshot["promptVersion"]!.GetValue<string>(), snapshot["pricingVersion"]!.GetValue<string>()));
            Assert.Equal("S01/v1", snapshot["scenarioVersion"]!.GetValue<string>());
            Assert.Equal(ScenarioCatalog.Latest("S01")!.FileHash, snapshot["scenarioHash"]!.GetValue<string>());
            Assert.Equal("finmind-tools/v1", snapshot["toolsetVersion"]!.GetValue<string>());
            Assert.Equal("finance-policy/v1", snapshot["policyVersion"]!.GetValue<string>());
            Assert.Equal(LedgerReasonCodes.CatalogVersion, snapshot["reasonCodeCatalog"]!.GetValue<string>());
            Assert.Equal("abc123", snapshot["codeRevision"]!.GetValue<string>());
            Assert.Equal(1_000_000, snapshot["startingBalanceSen"]!.GetValue<long>());

            var created = Assert.Single(ledger.GetEvents(run.RunId, 0, 100));
            Assert.Equal("RUN_CREATED", created.EventType);
            Assert.Equal(2, created.EventVersion);
            Assert.Equal(run.ConfigHash, created.Payload["configHash"]!.GetValue<string>());
            Assert.Null(created.Payload["objective"]);
        },
        ["full_lifecycle_funds_from_snapshot_and_records_transitions"] = ledger =>
        {
            var (run, cash) = Running(ledger);

            Assert.Equal("RUNNING", run.Status);
            Assert.NotNull(run.QueuedAt);
            Assert.NotNull(run.StartedAt);
            Assert.Null(run.FinishedAt);
            Assert.Equal(1_000_000, ledger.GetBalance(run.RunId, cash));
            var merchant = ledger.CreateAccount(run.RunId, AccountKind.Merchant);
            var limit = Assert.ThrowsAny<LedgerException>(() => ledger.Transfer(run.RunId, "big", cash, merchant, 50_001));
            Assert.Equal(LedgerReasonCode.ExceedsTransactionLimit, limit.ReasonCode);
            ledger.Transfer(run.RunId, "ok", cash, merchant, 50_000);

            var done = ledger.TransitionRun(run.RunId, RunStatus.Completed, null, Source);
            Assert.Equal("COMPLETED", done.Status);
            Assert.NotNull(done.FinishedAt);

            var transitions = ledger.GetEvents(run.RunId, 0, 500).Where(e => e.EventType == "RUN_STATUS_CHANGED")
                .Select(e => (e.Payload["from"]!.GetValue<string>(), e.Payload["to"]!.GetValue<string>())).ToArray();
            Assert.Equal([("DRAFT", "QUEUED"), ("QUEUED", "INITIALIZING"), ("INITIALIZING", "RUNNING"), ("RUNNING", "COMPLETED")], transitions);
            Assert.DoesNotContain(ledger.GetEvents(run.RunId, 0, 500), e => e.EventType == "RUN_CREATED" && e.EventVersion == 1);
        },
        ["disallowed_transitions_are_rejected_and_recorded"] = ledger =>
        {
            var run = ledger.CreateManagedRun(ValidConfig(), null);
            AssertTransitionRejected(ledger, run.RunId, () => ledger.TransitionRun(run.RunId, RunStatus.Completed, null, Source), "DRAFT");
            AssertTransitionRejected(ledger, run.RunId, () => ledger.TransitionRun(run.RunId, RunStatus.Initializing, null, Source), "DRAFT");
            AssertTransitionRejected(ledger, run.RunId, () => ledger.ActivateRun(run.RunId, Source), "DRAFT");
            ledger.TransitionRun(run.RunId, RunStatus.Queued, null, Source);
            ledger.TransitionRun(run.RunId, RunStatus.Initializing, null, Source);
            // RUNNING is entered only through activation, which funds the run.
            AssertTransitionRejected(ledger, run.RunId, () => ledger.TransitionRun(run.RunId, RunStatus.Running, null, Source), "INITIALIZING");
            ledger.TransitionRun(run.RunId, RunStatus.Cancelled, "researcher stopped it", Source);
            foreach (var to in Enum.GetValues<RunStatus>())
                AssertTransitionRejected(ledger, run.RunId, () => ledger.TransitionRun(run.RunId, to, "again", Source), "CANCELLED");
            AssertTransitionRejected(ledger, run.RunId, () => ledger.ActivateRun(run.RunId, Source), "CANCELLED");
        },
        ["terminal_transitions_require_a_reason"] = ledger =>
        {
            var run = ledger.CreateManagedRun(ValidConfig(), null);
            var missing = Assert.ThrowsAny<LedgerException>(() => ledger.TransitionRun(run.RunId, RunStatus.Cancelled, "  ", Source));
            Assert.Equal(LedgerReasonCode.MissingRequiredField, missing.ReasonCode);
            Assert.Equal("DRAFT", ledger.GetRun(run.RunId).Status);
            var cancelled = ledger.TransitionRun(run.RunId, RunStatus.Cancelled, new string('x', 600), Source);
            Assert.Equal(500, cancelled.StatusReason!.Length);
        },
        ["money_moves_only_while_running"] = ledger =>
        {
            var run = ledger.CreateManagedRun(ValidConfig(), null);
            AssertNotActive(ledger, run.RunId, () => ledger.CreateAccount(run.RunId, AccountKind.Merchant));
            AssertNotActive(ledger, run.RunId, () => ledger.OpenRun(run.RunId, "direct", 5, 5));

            var (running, cash) = Running(ledger);
            var merchant = ledger.CreateAccount(running.RunId, AccountKind.Merchant);
            var obligation = ledger.CreateObligation(running.RunId, merchant, 100, "INV-1");
            var payment = ledger.PayObligation(running.RunId, "pay-1", cash, obligation.ObligationId);
            AssertNotActive(ledger, running.RunId, () => ledger.OpenRun(running.RunId, "direct", 5, 5));

            ledger.TransitionRun(running.RunId, RunStatus.Cancelled, "stop", Source);
            AssertNotActive(ledger, running.RunId, () => ledger.Transfer(running.RunId, "t", cash, merchant, 1));
            AssertNotActive(ledger, running.RunId, () => ledger.MoveToSavings(running.RunId, "s", cash, merchant, 1));
            AssertNotActive(ledger, running.RunId, () => ledger.PayObligation(running.RunId, "pay-1", cash, obligation.ObligationId));
            AssertNotActive(ledger, running.RunId, () => ledger.ReverseJournal(running.RunId, "r", payment.JournalId, "late", ReversalInitiator.Researcher));
            AssertNotActive(ledger, running.RunId, () => ledger.CreateObligation(running.RunId, merchant, 5, "INV-2"));
            AssertNotActive(ledger, running.RunId, () => ledger.CreateAccount(running.RunId, AccountKind.Merchant));

            // Reads still work on a terminal run.
            Assert.Equal(999_900, ledger.GetBalance(running.RunId, cash));
            Assert.True(ledger.GetObligation(running.RunId, obligation.ObligationId).Paid);
        },
        ["legacy_and_unknown_runs"] = ledger =>
        {
            var legacy = Guid.NewGuid();
            ledger.OpenRun(legacy, "open", 1_000, 100);
            Assert.False(ledger.GetRun(legacy).Managed);
            var code = Assert.ThrowsAny<LedgerException>(() => ledger.TransitionRun(legacy, RunStatus.Queued, null, Source)).ReasonCode;
            Assert.Equal(LedgerReasonCode.InvalidRunTransition, code);

            var unknown = Guid.NewGuid();
            Assert.Equal(LedgerReasonCode.RunNotFound,
                Assert.ThrowsAny<LedgerException>(() => ledger.TransitionRun(unknown, RunStatus.Queued, null, Source)).ReasonCode);
            Assert.Equal(LedgerReasonCode.RunNotFound, Assert.ThrowsAny<LedgerException>(() => ledger.GetRun(unknown)).ReasonCode);
            Assert.Empty(ledger.GetEvents(unknown, 0, 10));
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

    public static TheoryData<string, string[]> InvalidConfigs() => new()
    {
        { "unknown_field", ["extra"] },
        { "missing_field", ["seed"] },
        { "fraction", ["startingBalanceSen"] },
        { "number_as_string", ["maxActions"] },
        { "limit_above_balance", ["transactionLimitSen"] },
        { "out_of_range", ["durationDays", "seed"] },
        { "unknown_scenario", ["scenarioId"] },
        { "unknown_provider", ["agent.provider"] },
        { "unknown_model", ["agent.modelId"] },
        { "mock_sampling", ["agent.sampling"] },
        { "agent_unknown_field", ["agent.apiKey"] },
        { "blank_objective", ["objective"] },
        { "bad_memory_mode", ["memoryMode"] },
    };

    private static JsonObject InvalidConfig(string name) => name switch
    {
        "unknown_field" => With(c => c["extra"] = 1),
        "missing_field" => With(c => c.Remove("seed")),
        "fraction" => With(c => c["startingBalanceSen"] = 10.5),
        "number_as_string" => With(c => c["maxActions"] = "100"),
        "limit_above_balance" => With(c => c["transactionLimitSen"] = 1_000_001),
        "out_of_range" => With(c => { c["durationDays"] = 0; c["seed"] = -1; }),
        "unknown_scenario" => With(c => c["scenarioId"] = "S99"),
        "unknown_provider" => With(c => c["agent"]!["provider"] = "openai"),
        "unknown_model" => With(c => c["agent"]!["modelId"] = "gpt-x"),
        "mock_sampling" => With(c => c["agent"]!["sampling"] = new JsonObject { ["temperature"] = 0 }),
        "agent_unknown_field" => With(c => c["agent"]!["apiKey"] = "sk-test"),
        "blank_objective" => With(c => c["objective"] = "   "),
        "bad_memory_mode" => With(c => c["memoryMode"] = "infinite"),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void Invalid_configuration_names_fields_without_values(string name, string[] fields)
    {
        var exception = Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(InvalidConfig(name), null));
        Assert.Equal(fields.Order(StringComparer.Ordinal), exception.Fields);

        var (statusCode, body) = LedgerHttpResponses.ForException(exception);
        Assert.Equal(400, statusCode);
        Assert.Equal("INVALID_RUN_CONFIG", body.ReasonCode);
        Assert.Equal(exception.Fields, body.Fields);
        Assert.DoesNotContain("sk-test", body.Message);
    }

    [Fact]
    public void Missing_body_is_invalid_configuration() =>
        Assert.Equal(["body"], Assert.Throws<RunConfigException>(() => new InMemoryLedger().CreateManagedRun(null, null)).Fields);

    [PostgresFact]
    public void Postgres_database_protects_snapshot_and_lifecycle()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var run = ledger.CreateManagedRun(ValidConfig(), null);
        var legacy = Guid.NewGuid();
        ledger.OpenRun(legacy, "open", 1_000, 100);

        AssertSqlFails(dataSource, "UPDATE experiment_runs SET config_snapshot = '{}' WHERE id = @id", run.RunId, "55000");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET config_hash = '\\x00' WHERE id = @id", run.RunId, "55000");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET status = 'RUNNING' WHERE id = @id", run.RunId, "23514");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET status = 'DRAFT' WHERE id = @id", legacy, "55000");
        AssertSqlFails(dataSource,
            "INSERT INTO experiment_runs (id, transaction_limit_sen, status, config_snapshot) VALUES (gen_random_uuid(), 1, 'RUNNING', '{}') RETURNING @id",
            run.RunId, "23514");

        ledger.TransitionRun(run.RunId, RunStatus.Cancelled, "stop", Source);
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET status = 'QUEUED' WHERE id = @id", run.RunId, "23514");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET status_reason = 'edited' WHERE id = @id", run.RunId, "55000");

        Assert.Equal(run.ConfigHash, ledger.GetRun(run.RunId).ConfigHash);
        Assert.Equal("CANCELLED", ledger.GetRun(run.RunId).Status);
    }

    [PostgresFact]
    public void Postgres_hash_matches_canonical_snapshot_text()
    {
        using var dataSource = OwnerDataSource();
        var run = new PostgresLedger(dataSource).CreateManagedRun(ValidConfig(), null);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT encode(finmind_run_config_hash(config_snapshot), 'hex') FROM experiment_runs WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", run.RunId);
        Assert.Equal(run.ConfigHash, command.ExecuteScalar());
    }

    [PostgresFact]
    public void Postgres_api_role_cannot_change_snapshot()
    {
        var apiConnectionString = Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")
            ?? throw new InvalidOperationException("FINMIND_API_TEST_DATABASE is required.");
        using var dataSource = NpgsqlDataSource.Create(apiConnectionString);
        var ledger = new PostgresLedger(dataSource);
        var (run, cash) = Running(ledger);
        Assert.Equal(1_000_000, ledger.GetBalance(run.RunId, cash));

        AssertSqlFails(dataSource, "UPDATE experiment_runs SET config_snapshot = '{}' WHERE id = @id", run.RunId, "42501");
        AssertSqlFails(dataSource, "UPDATE experiment_runs SET started_at = now() WHERE id = @id", run.RunId, "42501");
        ledger.TransitionRun(run.RunId, RunStatus.Completed, null, Source);
        Assert.Equal("COMPLETED", ledger.GetRun(run.RunId).Status);
    }

    [PostgresFact]
    public async Task Postgres_cancel_racing_transfers_splits_cleanly()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var (run, cash) = Running(ledger);
        var merchant = ledger.CreateAccount(run.RunId, AccountKind.Merchant);

        using var start = new Barrier(11);
        var transfers = Enumerable.Range(0, 10).Select(index => Task.Run(() =>
        {
            start.SignalAndWait();
            try { ledger.Transfer(run.RunId, $"t-{index}", cash, merchant, 100); return (LedgerReasonCode?)null; }
            catch (LedgerException ex) { return ex.ReasonCode; }
        })).ToArray();
        var cancel = Task.Run(() =>
        {
            start.SignalAndWait();
            ledger.TransitionRun(run.RunId, RunStatus.Cancelled, "stop", Source);
        });
        var results = await Task.WhenAll(transfers);
        await cancel;

        Assert.All(results, code => Assert.True(code is null or LedgerReasonCode.RunNotActive));
        var events = ledger.GetEvents(run.RunId, 0, 500);
        var cancelSequence = events.Single(e => e.EventType == "RUN_STATUS_CHANGED" && e.Payload["to"]!.GetValue<string>() == "CANCELLED").Sequence;
        Assert.All(events.Where(e => e.Operation == "transfer" && e.EventType == "LEDGER_COMMITTED"), e => Assert.True(e.Sequence < cancelSequence));
        Assert.All(events.Where(e => e.Operation == "transfer" && e.EventType == "POLICY_DENIED"), e => Assert.True(e.Sequence > cancelSequence));
        var committed = results.Count(code => code is null);
        Assert.Equal(1_000_000 - 100L * committed, ledger.GetBalance(run.RunId, cash));
    }

    private static void AssertTransitionRejected(IFinanceLedger ledger, Guid runId, Action act, string expectedStatus)
    {
        var before = ledger.GetEvents(runId, 0, 500).Count;
        var exception = Assert.ThrowsAny<LedgerException>(act);
        Assert.Equal(LedgerReasonCode.InvalidRunTransition, exception.ReasonCode);
        Assert.Equal(expectedStatus, ledger.GetRun(runId).Status);
        var denial = Assert.Single(ledger.GetEvents(runId, before, 500));
        Assert.Equal("POLICY_DENIED", denial.EventType);
        Assert.Equal("INVALID_RUN_TRANSITION", denial.ReasonCode);
    }

    private static void AssertNotActive(IFinanceLedger ledger, Guid runId, Action act)
    {
        var before = ledger.GetEvents(runId, 0, 500).Count;
        var journals = ledger.GetJournals(runId).Count;
        Assert.Equal(LedgerReasonCode.RunNotActive, Assert.ThrowsAny<LedgerException>(act).ReasonCode);
        Assert.Equal(journals, ledger.GetJournals(runId).Count);
        var denial = Assert.Single(ledger.GetEvents(runId, before, 500));
        Assert.Equal("RUN_NOT_ACTIVE", denial.ReasonCode);
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
