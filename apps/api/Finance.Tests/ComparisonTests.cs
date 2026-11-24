using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0015 step 2: batches, start order, and comparison.
[Collection(QueuedRunTests.Name)]
public sealed class ComparisonTests
{
    private static JsonNode? Stat(JsonObject summary, string key) => summary[key];

    [Fact]
    public void Money_summary_matches_a_hand_calculation()
    {
        var s = Comparison.Summarize([100, 200, 300, 400, 500], ComparisonUnit.Sen);
        Assert.Equal(5, (int)Stat(s, "n")!);
        Assert.Equal("300.00", (string)Stat(s, "mean")!);  // exact decimal, rounded half to even
        Assert.Equal((100L, 500L), ((long)Stat(s, "min")!, (long)Stat(s, "max")!));
        Assert.Equal(158.11, (double)Stat(s, "sd")!);      // sample standard deviation
        // t(0.975, 4) = 2.776: 300 +/- 2.776 * 158.1139 / 2.2361 = 300 +/- 196.29
        Assert.Equal((103.71, 496.29), ((double)Stat(s, "ci95")!["low"]!, (double)Stat(s, "ci95")!["high"]!));
    }

    [Fact]
    public void Small_groups_get_no_interval_and_tiny_ones_no_spread()
    {
        Assert.Null(Stat(Comparison.Summarize([1, 2, 3], ComparisonUnit.Count), "ci95"));
        Assert.Equal("1.50", (string)Stat(Comparison.Summarize([1, 2], ComparisonUnit.Sen), "mean")!);
        Assert.Equal("1.33", (string)Stat(Comparison.Summarize([1, 1, 2], ComparisonUnit.Sen), "mean")!);
        var one = Comparison.Summarize([7], ComparisonUnit.Count);
        Assert.Null(Stat(one, "sd"));
        var none = Comparison.Summarize([], ComparisonUnit.Count);
        Assert.Equal((0, (string?)null), ((int)Stat(none, "n")!, (string?)Stat(none, "mean")));
    }

    [Fact]
    public void Rates_that_do_not_apply_are_counted_but_not_averaged()
    {
        var s = Comparison.SummarizeRates([0.25, null, 0.5]);
        Assert.Equal((2, 1), ((int)Stat(s, "n")!, (int)Stat(s, "notApplicable")!));
        Assert.Equal(0.375, (double)Stat(s, "mean")!);
    }

    private static JsonObject Snapshot(string scenario = "S01", long seed = 42, string revision = "r1") => new()
    {
        ["objective"] = "o", ["scenarioId"] = scenario, ["seed"] = seed, ["startingBalanceSen"] = 100, ["transactionLimitSen"] = 50,
        ["durationDays"] = 30, ["maxActions"] = 10, ["maxModelTokens"] = 0, ["maxWallTimeSeconds"] = 60, ["maxCostMicroUsd"] = 0,
        ["memoryMode"] = "none", ["describeUnavailableCapabilities"] = false,
        ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1", ["settings"] = new JsonObject() },
        ["scenarioVersion"] = $"{scenario}/v1", ["codeRevision"] = revision, ["promptHash"] = null
    };

    [Fact]
    public void Differences_are_named_between_and_within_groups()
    {
        var differences = Comparison.Differences([
            [Snapshot(), Snapshot()],
            [Snapshot("S03"), Snapshot("S03", seed: 7, revision: "r2")],
        ]);
        Assert.Equal(["scenarioId"], differences["betweenGroups"]!.AsArray().Select(f => (string)f!));
        Assert.Equal(["seed"], differences["withinGroups"]![1]!.AsArray().Select(f => (string)f!));
        Assert.Empty(differences["withinGroups"]![0]!.AsArray());
        Assert.Equal(["codeRevision", "scenarioVersion"],
            differences["metadata"]!.AsArray().Select(f => (string)f!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true, "COMPLETED", null, null)]
    [InlineData(false, null, null, "development run")]
    [InlineData(true, "RUNNING", null, "not finished")]
    [InlineData(true, "FAILED", "runtime: API unreachable", "platform failure")]
    [InlineData(true, "FAILED", "Worker session expired.", "platform failure")]
    [InlineData(true, "FAILED", "model refusal (cyber)", null)]
    [InlineData(true, "FAILED", "unrepresentable tool input", null)]
    [InlineData(true, "BUDGET_EXCEEDED", "Wall-time budget exhausted.", null)]
    [InlineData(true, "CANCELLED", "researcher stopped it", "cancelled")]
    public void Exclusions_are_explicit(bool eligible, string? status, string? reason, string? expected) =>
        Assert.Equal(expected, Comparison.ExclusionReason(eligible, status, reason));

    [Fact]
    public void Start_order_is_a_recorded_seeded_permutation()
    {
        var order = Comparison.StartOrder(10, 12345);
        Assert.Equal(Enumerable.Range(0, 10), order.Order());
        Assert.Equal(order, Comparison.StartOrder(10, 12345));
        Assert.NotEqual(order, Comparison.StartOrder(10, 54321));
    }

    private static readonly JsonObject Config = new()
    {
        ["objective"] = "o", ["scenarioId"] = "S01", ["seed"] = 1, ["startingBalanceSen"] = 100_000, ["transactionLimitSen"] = 50_000,
        ["durationDays"] = 30, ["maxActions"] = 10, ["maxModelTokens"] = 0, ["maxWallTimeSeconds"] = 60, ["memoryMode"] = "none",
        ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
    };

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["a_run_records_its_batch"] = ledger =>
        {
            var batch = Guid.NewGuid();
            var run = ledger.CreateManagedRun((JsonObject)Config.DeepClone(), null, batch, 2);
            Assert.Equal((batch, 2), (ledger.GetRun(run.RunId).BatchId, ledger.GetRun(run.RunId).BatchPosition));
            Assert.Null(ledger.CreateManagedRun((JsonObject)Config.DeepClone(), null).BatchId);
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
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        using var api = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        // The batch row comes first (foreign key); runs are then created through the restricted role.
        var reads = new BatchStore(api);
        var ledger = new BatchingLedger(new PostgresLedger(api), reads);
        Scenarios[scenario](ledger);
    }

    [PostgresFact]
    public void Batch_membership_cannot_change()
    {
        using var owner = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        using var api = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var store = new BatchStore(api);
        var batch = store.Insert("immutability check", (JsonObject)Config.DeepClone(), 1, "same", 9, [0]);
        var run = new PostgresLedger(api).CreateManagedRun((JsonObject)Config.DeepClone(), null, batch, 0).RunId;

        using var connection = owner.OpenConnection();
        foreach (var change in new[] { "batch_id = NULL", "batch_position = 5", "batch_id = gen_random_uuid()" })
        {
            using var update = new NpgsqlCommand($"UPDATE experiment_runs SET {change} WHERE id = @id", connection);
            update.Parameters.AddWithValue("id", run);
            Assert.Throws<PostgresException>(() => update.ExecuteNonQuery());
        }
        using var edit = new NpgsqlCommand("UPDATE run_batches SET label = 'x' WHERE id = @id", connection);
        edit.Parameters.AddWithValue("id", batch);
        Assert.Throws<PostgresException>(() => edit.ExecuteNonQuery());
    }
}

// Test helper: creates the batch row a run refers to, then delegates.
internal sealed class BatchingLedger(PostgresLedger inner, BatchStore store) : IFinanceLedger
{
    public RunInfo CreateManagedRun(JsonObject? config, string? codeRevision, Guid? batchId = null, int? batchPosition = null)
    {
        if (batchId is { } id) store.InsertWithId(id, "test", config!, 1, "same", 1, [0]);
        return inner.CreateManagedRun(config, codeRevision, batchId, batchPosition);
    }
    public RunInfo GetRun(Guid runId) => inner.GetRun(runId);
    public Guid CreateAccount(Guid runId, AccountKind kind) => throw new NotSupportedException();
    public OpenRunResult OpenRun(Guid runId, string idempotencyKey, long openingBalanceSen, long transactionLimitSen) => throw new NotSupportedException();
    public CommitResult Transfer(Guid runId, string idempotencyKey, Guid sourceAccountId, Guid destinationAccountId, long amountSen) => throw new NotSupportedException();
    public CommitResult MoveToSavings(Guid runId, string idempotencyKey, Guid cashAccountId, Guid savingsAccountId, long amountSen) => throw new NotSupportedException();
    public ObligationResult CreateObligation(Guid runId, Guid merchantAccountId, long amountSen, string invoiceReference) => throw new NotSupportedException();
    public ObligationResult GetObligation(Guid runId, Guid obligationId) => throw new NotSupportedException();
    public CommitResult PayObligation(Guid runId, string idempotencyKey, Guid cashAccountId, Guid obligationId) => throw new NotSupportedException();
    public CommitResult ReverseJournal(Guid runId, string idempotencyKey, Guid originalJournalId, string reason, ReversalInitiator initiator) => throw new NotSupportedException();
    public long GetBalance(Guid runId, Guid accountId) => throw new NotSupportedException();
    public IReadOnlyList<LedgerJournal> GetJournals(Guid runId) => throw new NotSupportedException();
    public IReadOnlyList<RunEvent> GetEvents(Guid runId, long afterSequence, int limit) => throw new NotSupportedException();
    public RunInfo TransitionRun(Guid runId, RunStatus to, string? reason, string source) => throw new NotSupportedException();
    public OpenRunResult ActivateRun(Guid runId, string source) => throw new NotSupportedException();
    public ClockResult AdvanceClock(Guid runId, int days, string source) => throw new NotSupportedException();
    public void EnsureDirectWriteAllowed(Guid runId, string operation, JsonObject request) => throw new NotSupportedException();
    public EnvironmentView GetEnvironment(Guid runId) => throw new NotSupportedException();
    public RunInfo? ClaimQueuedRun(byte[] tokenHash, DateTimeOffset now, DateTimeOffset expiresAt) => throw new NotSupportedException();
    public int FailRunsWithExpiredSessions(DateTimeOffset now) => throw new NotSupportedException();
    public Guid? FindSession(byte[] tokenHash) => throw new NotSupportedException();
    public ModelReservationResult ReserveModelCall(Guid runId, ModelReservation reservation) => throw new NotSupportedException();
    public Guid? ResolveSession(byte[] tokenHash, DateTimeOffset now, DateTimeOffset newExpiresAt) => throw new NotSupportedException();
    public ToolCallBegin BeginToolCall(Guid runId, string toolCallId, string tool, JsonObject redactedArguments, string requestHash) => throw new NotSupportedException();
    public JsonObject CompleteToolCall(Guid runId, string toolCallId, string tool, ToolCallCompletion completion) => throw new NotSupportedException();
    public AgentView GetAgentView(Guid runId) => throw new NotSupportedException();
    public ModelInvocationResult RecordModelInvocation(Guid runId, ModelInvocation invocation) => throw new NotSupportedException();
}
