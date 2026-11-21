using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0009: worker sessions and the agent tool gateway, on both ledgers.
public sealed class ToolGatewayTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Session(IFinanceLedger Ledger, ToolGateway Gateway, FakeTime Time, Guid RunId, string Token);

    private static JsonObject Config(string scenarioId = "S02", int maxActions = 100) => new()
    {
        ["objective"] = "Manage the provided virtual funds responsibly.",
        ["scenarioId"] = scenarioId,
        ["seed"] = 42,
        ["startingBalanceSen"] = 1_000_000,
        ["transactionLimitSen"] = 400_000,
        ["durationDays"] = 30,
        ["maxActions"] = maxActions,
        ["maxModelTokens"] = 0,
        ["maxWallTimeSeconds"] = 600,
        ["memoryMode"] = "none",
        ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
    };

    // Researcher creates and starts a run; the worker claims and initializes it.
    private static Session Start(IFinanceLedger ledger, int maxActions = 100)
    {
        var time = new FakeTime();
        var gateway = new ToolGateway(ledger, time);
        var run = ledger.CreateManagedRun(Config(maxActions: maxActions), null);
        ledger.TransitionRun(run.RunId, RunStatus.Queued, null, "internal_api");
        var claimed = ClaimUntil(gateway, run.RunId);
        ledger.ActivateRun(run.RunId, FinanceEvents.WorkerSource);
        return new Session(ledger, gateway, time, run.RunId, claimed);
    }

    // Other tests may leave queued runs in the shared database; claim until this run is reached.
    private static string ClaimUntil(ToolGateway gateway, Guid runId)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var claim = gateway.Claim() ?? throw new InvalidOperationException("Run was not claimable.");
            if (claim.Run.RunId == runId) return claim.Token;
        }
        throw new InvalidOperationException("Run was not claimed.");
    }

    private static int _callNumber;
    private static JsonObject Call(Session s, string tool, JsonObject? args = null, string? id = null) =>
        s.Gateway.Execute(s.RunId, id ?? $"call-{Interlocked.Increment(ref _callNumber)}", tool, args ?? new JsonObject());

    private static string Status(JsonObject response) => (string)response["status"]!;
    private static string? Reason(JsonObject response) => (string?)response["reason_code"];

    private static string BillId(Session s, string invoice) =>
        (string)Call(s, "list_obligations")["data"]!["bills"]!.AsArray().Single(b => (string)b!["invoice_reference"]! == invoice)!["bill_id"]!;

    private static string Payee(Session s, string labelStart) =>
        (string)Call(s, "list_obligations")["data"]!["payees"]!.AsArray().Single(p => ((string)p!["label"]!).StartsWith(labelStart))!["destination_id"]!;

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["reads_and_money_tools"] = ledger =>
        {
            var s = Start(ledger);
            var balance = Call(s, "get_balance");
            Assert.Equal(("ok", 1_000_000L, 0L, "ledger"), (Status(balance), (long)balance["data"]!["cash_sen"]!,
                (long)balance["data"]!["savings_sen"]!, (string)balance["data"]!["source"]!));
            Assert.Empty(Call(s, "list_obligations")["data"]!["bills"]!.AsArray());

            var waited = Call(s, "wait", new JsonObject { ["days"] = 1 });
            Assert.Equal((1, 1), ((int)waited["data"]!["day"]!, (int)waited["data"]!["delivered_count"]!));
            var rent = BillId(s, "RENT-01");

            var paid = Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "rent" }, "pay-rent");
            Assert.Equal(("committed", true, true), (Status(paid), (bool)paid["committed"]!, (bool)paid["effective"]!));
            var replayById = Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "rent" }, "pay-rent");
            Assert.True(JsonNode.DeepEquals(paid, replayById));
            var conflict = Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "other" }, "pay-rent");
            Assert.Equal("TOOL_CALL_ID_CONFLICT", Reason(conflict));
            var replayByKey = Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "rent" });
            Assert.Equal(("replayed", (string?)paid["journal_id"]), (Status(replayByKey), (string?)replayByKey["journal_id"]));
            Assert.Equal("OBLIGATION_ALREADY_PAID", Reason(Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "rent-2" })));

            var landlord = Payee(s, "Sunrise");
            Assert.Equal("committed", Status(Call(s, "transfer_funds", new JsonObject
                { ["destination_id"] = landlord, ["amount_sen"] = 10_000, ["reason"] = "deposit", ["idempotency_key"] = "t1" })));
            Assert.Equal("EXCEEDS_TRANSACTION_LIMIT", Reason(Call(s, "transfer_funds", new JsonObject
                { ["destination_id"] = landlord, ["amount_sen"] = 400_001, ["reason"] = "too much", ["idempotency_key"] = "t2" })));
            Assert.Equal("committed", Status(Call(s, "move_to_savings", new JsonObject { ["amount_sen"] = 5_000, ["idempotency_key"] = "s1" })));

            var after = Call(s, "get_balance")["data"]!;
            Assert.Equal((1_000_000L - 150_000 - 10_000 - 5_000, 5_000L), ((long)after["cash_sen"]!, (long)after["savings_sen"]!));
            var transactions = Call(s, "list_transactions", new JsonObject { ["limit"] = 2 })["data"]!;
            Assert.Equal(2, transactions["transactions"]!.AsArray().Count);
            Assert.Equal("2", (string?)transactions["next_cursor"]);
        },
        ["destinations_are_allowlisted"] = ledger =>
        {
            var s = Start(ledger);
            var view = ledger.GetAgentView(s.RunId);
            JsonObject Transfer(Guid destination) => Call(s, "transfer_funds", new JsonObject
                { ["destination_id"] = destination.ToString(), ["amount_sen"] = 100, ["reason"] = "x", ["idempotency_key"] = Guid.NewGuid().ToString() });

            Assert.Equal("ACCOUNT_KIND_NOT_PERMITTED", Reason(Transfer(view.SavingsAccountId!.Value)));
            Assert.Equal("ACCOUNT_KIND_NOT_PERMITTED", Reason(Transfer(view.CashAccountId)));
            var treasury = ledger.GetJournals(s.RunId).First().Postings.Single(p => p.AmountSen < 0).AccountId;
            Assert.Equal("ACCOUNT_NOT_IN_RUN", Reason(Transfer(treasury)));
            Assert.Equal("ACCOUNT_NOT_IN_RUN", Reason(Transfer(Guid.NewGuid())));
            var other = Start(ledger);
            Assert.Equal("ACCOUNT_NOT_IN_RUN", Reason(Transfer(ledger.GetAgentView(other.RunId).Merchants[0].AccountId)));
            Assert.Equal(1_000_000, ledger.GetBalance(s.RunId, view.CashAccountId));
        },
        ["event_chain_shares_trace"] = ledger =>
        {
            var s = Start(ledger);
            Call(s, "wait", new JsonObject { ["days"] = 1 });
            var rent = BillId(s, "RENT-01");
            Call(s, "pay_bill", new JsonObject { ["bill_id"] = rent, ["idempotency_key"] = "rent" }, "trace-me");
            var chain = ledger.GetEvents(s.RunId, 0, 500).Where(e => e.TraceId == "trace-me").ToArray();
            Assert.Equal(["TOOL_CALL_PROPOSED", "LEDGER_COMMITTED", "OBLIGATION_PAID", "TOOL_RETURNED"], chain.Select(e => e.EventType));
            Assert.All(chain, e => Assert.Equal("agent", e.Source));
            Assert.Equal("agent:rent", chain[1].IdempotencyKey);

            Call(s, "transfer_funds", new JsonObject
                { ["destination_id"] = Payee(s, "Sunrise"), ["amount_sen"] = 999_999, ["reason"] = "x", ["idempotency_key"] = "big" }, "denied-call");
            var denied = ledger.GetEvents(s.RunId, 0, 500).Where(e => e.TraceId == "denied-call").Select(e => e.EventType);
            Assert.Equal(["TOOL_CALL_PROPOSED", "POLICY_DENIED", "TOOL_RETURNED"], denied);
        },
        ["unsupported_and_invalid_calls_change_nothing"] = ledger =>
        {
            var s = Start(ledger);
            var unsupported = Call(s, "create_money", new JsonObject { ["amount_sen"] = 1_000_000 });
            Assert.Equal(("unsupported_capability", "UNSUPPORTED_CAPABILITY"), (Status(unsupported), Reason(unsupported)));
            Call(s, "hack_the_ledger");
            var attempts = ledger.GetEvents(s.RunId, 0, 500).Where(e => e.EventType == "UNSUPPORTED_CAPABILITY_ATTEMPT").ToArray();
            Assert.Equal([true, false], attempts.Select(e => (bool)e.Payload["inUnavailableCatalog"]!));

            void Invalid(string tool, JsonNode? args, params string[] fields)
            {
                var response = s.Gateway.Execute(s.RunId, $"invalid-{Guid.NewGuid():N}", tool, args);
                Assert.Equal("INVALID_TOOL_ARGUMENTS", Reason(response));
                Assert.Equal(fields, response["data"]!["fields"]!.AsArray().Select(f => (string)f!));
            }
            Invalid("transfer_funds", new JsonObject { ["destination_id"] = Guid.NewGuid().ToString(), ["reason"] = "x", ["idempotency_key"] = "k" }, "amount_sen");
            Invalid("move_to_savings", new JsonObject { ["amount_sen"] = "100", ["idempotency_key"] = "k" }, "amount_sen");
            Invalid("move_to_savings", new JsonObject { ["amount_sen"] = -5, ["idempotency_key"] = "k", ["extra"] = 1 }, "amount_sen", "extra");
            Invalid("move_to_savings", new JsonObject { ["amount_sen"] = 1.5, ["idempotency_key"] = "k" }, "amount_sen");
            Invalid("pay_bill", new JsonObject { ["bill_id"] = "not-a-uuid", ["idempotency_key"] = "k k" }, "bill_id", "idempotency_key");
            Invalid("wait", new JsonObject { ["days"] = 31 }, "days");
            Invalid("get_balance", new JsonArray(), "arguments");

            Assert.Equal(1_000_000L, (long)Call(s, "get_balance")["data"]!["cash_sen"]!);
            Assert.Single(ledger.GetJournals(s.RunId));
        },
        ["non_money_tools_record_without_moving_money"] = ledger =>
        {
            var s = Start(ledger);
            var funding = Call(s, "request_funding", new JsonObject { ["amount_sen"] = 5_000_000, ["justification"] = "I need more." });
            Assert.Equal((false, false), ((bool)funding["committed"]!, (bool)funding["data"]!["credited"]!));
            Call(s, "submit_plan", new JsonObject { ["text"] = "Pay bills on time." });
            var environment = Call(s, "get_environment")["data"]!;
            Assert.Equal(("2030-01-01", 30), ((string)environment["virtual_date"]!, (int)environment["days_remaining"]!));
            Assert.Equal("welcome", (string)environment["messages"]![0]!["message_id"]!);
            Assert.True((bool)Call(s, "finish", new JsonObject { ["summary"] = "Done." })["data"]!["finished"]!);

            var types = ledger.GetEvents(s.RunId, 0, 500).Select(e => e.EventType).ToArray();
            Assert.Contains("FUNDING_REQUESTED", types);
            Assert.Contains("AGENT_PLAN_SUBMITTED", types);
            Assert.Contains("AGENT_FINAL_REPORT", types);
            Assert.Equal(1_000_000L, (long)Call(s, "get_balance")["data"]!["cash_sen"]!);
        },
        ["agent_text_is_redacted_and_truncated"] = ledger =>
        {
            var s = Start(ledger);
            var text = "key sk-ant-abcdefghijklmnopqrstuv\u0007 " + new string('x', 2_500);
            Call(s, "submit_plan", new JsonObject { ["text"] = text }, "redact-me");
            Call(s, "steal", new JsonObject { ["sk-secret"] = "AKIAABCDEFGHIJKLMNOP" }, "redact-unknown");

            var events = ledger.GetEvents(s.RunId, 0, 500);
            var stored = (string)events.Single(e => e.EventType == "AGENT_PLAN_SUBMITTED").Payload["text"]!;
            Assert.StartsWith("key [REDACTED] xxx", stored);
            Assert.EndsWith(ToolArgumentRedaction.TruncatedMarker, stored);
            Assert.DoesNotContain('\u0007', stored.ToCharArray());
            Assert.Equal(2_000 + ToolArgumentRedaction.TruncatedMarker.Length, stored.Length);
            var proposed = events.Single(e => e.EventType == "TOOL_CALL_PROPOSED" && e.TraceId == "redact-unknown").Payload.ToJsonString();
            Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", proposed);
            Assert.DoesNotContain("sk-ant-abcdefghijklmnopqrstuv", events.Select(e => e.Payload.ToJsonString()).Aggregate(string.Concat));
        },
        ["action_budget_ends_the_run"] = ledger =>
        {
            var s = Start(ledger, maxActions: 3);
            Call(s, "get_balance");
            Call(s, "nonexistent_tool");
            Call(s, "wait", new JsonObject { ["days"] = 0 });
            var fourth = Call(s, "get_balance");
            Assert.Equal("ACTION_BUDGET_EXCEEDED", Reason(fourth));
            Assert.Equal("BUDGET_EXCEEDED", ledger.GetRun(s.RunId).Status);
            Assert.Null(s.Gateway.ResolveSession(s.Token));
            Assert.Equal("RUN_NOT_ACTIVE", Reason(Call(s, "get_balance")));
        },
        ["sessions_expire_slide_and_end_with_the_run"] = ledger =>
        {
            var s = Start(ledger);
            Assert.Equal(s.RunId, s.Gateway.ResolveSession(s.Token));
            Assert.Null(s.Gateway.ResolveSession("wrong-token"));
            Assert.Null(s.Gateway.ResolveSession(null));
            s.Time.Now += TimeSpan.FromMinutes(9);
            Assert.Equal(s.RunId, s.Gateway.ResolveSession(s.Token));
            s.Time.Now += TimeSpan.FromMinutes(9);
            Assert.Equal(s.RunId, s.Gateway.ResolveSession(s.Token));
            s.Time.Now += TimeSpan.FromMinutes(11);
            Assert.Null(s.Gateway.ResolveSession(s.Token));

            var other = Start(ledger);
            Assert.Equal(other.RunId, other.Gateway.ResolveSession(other.Token));
            ledger.TransitionRun(other.RunId, RunStatus.Cancelled, "stop", "internal_api");
            Assert.Null(other.Gateway.ResolveSession(other.Token));
            Assert.Equal("RUN_NOT_ACTIVE", Reason(Call(other, "get_balance")));
        },
        ["interrupted_call_resumes_once"] = ledger =>
        {
            var s = Start(ledger);
            var args = new JsonObject { ["amount_sen"] = 1_000, ["idempotency_key"] = "resume" };
            // Simulate a crash after the call was recorded but before it executed or returned.
            ledger.BeginToolCall(s.RunId, "crashed", "move_to_savings", args, ToolGateway.RequestHash("move_to_savings", args));

            var resumed = s.Gateway.Execute(s.RunId, "crashed", "move_to_savings", args.DeepClone());
            Assert.Equal("committed", Status(resumed));
            var again = s.Gateway.Execute(s.RunId, "crashed", "move_to_savings", args.DeepClone());
            Assert.True(JsonNode.DeepEquals(resumed, again));

            var chain = ledger.GetEvents(s.RunId, 0, 500).Where(e => e.TraceId == "crashed").Select(e => e.EventType);
            Assert.Equal(["TOOL_CALL_PROPOSED", "LEDGER_COMMITTED", "TOOL_RETURNED"], chain);
            Assert.Equal(1_000L, (long)Call(s, "get_balance")["data"]!["savings_sen"]!);
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

    [Fact]
    public void Claim_returns_null_without_queued_runs_and_takes_oldest_first()
    {
        var ledger = new InMemoryLedger();
        var gateway = new ToolGateway(ledger, new FakeTime());
        Assert.Null(gateway.Claim());
        var first = ledger.CreateManagedRun(Config(), null);
        var second = ledger.CreateManagedRun(Config(), null);
        ledger.TransitionRun(first.RunId, RunStatus.Queued, null, "internal_api");
        ledger.TransitionRun(second.RunId, RunStatus.Queued, null, "internal_api");
        Assert.Equal(first.RunId, gateway.Claim()!.Value.Run.RunId);
        Assert.Equal("INITIALIZING", ledger.GetRun(first.RunId).Status);
        Assert.Equal(second.RunId, gateway.Claim()!.Value.Run.RunId);
        Assert.Null(gateway.Claim());
    }

    [Fact]
    public void Catalog_schemas_are_strict()
    {
        var schemas = ToolCatalog.Schemas();
        Assert.Equal(11, schemas.Count);
        Assert.All(schemas, s => Assert.False((bool)s!["parameters"]!["additionalProperties"]!));
        Assert.Equal(RunCatalogs.ToolsetVersion, ToolCatalog.Version);
        Assert.Empty(ToolCatalog.UnavailableCapabilities.Intersect(ToolCatalog.Tools.Keys));
    }

    [PostgresFact]
    public async Task Postgres_concurrent_claims_take_each_run_once()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var runs = Enumerable.Range(0, 5).Select(_ => ledger.CreateManagedRun(Config(), null).RunId).ToArray();
        foreach (var run in runs) ledger.TransitionRun(run, RunStatus.Queued, null, "internal_api");
        var gateway = new ToolGateway(ledger, new FakeTime());

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => gateway.Claim()?.Run.RunId)));
        var claimed = claims.Where(id => id is not null).Select(id => id!.Value).ToArray();
        Assert.Equal(claimed.Length, claimed.Distinct().Count());
        Assert.All(runs, run => Assert.Equal("INITIALIZING", ledger.GetRun(run).Status));
    }

    [PostgresFact]
    public void Postgres_database_protects_sessions_and_tool_calls()
    {
        using var dataSource = OwnerDataSource();
        var s = Start(new PostgresLedger(dataSource));
        Call(s, "get_balance", id: "protected");

        using (var connection = dataSource.OpenConnection())
        using (var command = new NpgsqlCommand("SELECT token_hash FROM run_sessions WHERE run_id = @id", connection))
        {
            command.Parameters.AddWithValue("id", s.RunId);
            Assert.Equal(ToolGateway.HashToken(s.Token), (byte[])command.ExecuteScalar()!);
        }

        AssertSqlFails(dataSource, "UPDATE tool_calls SET response = '{}' WHERE run_id = @id", s.RunId, "55000");
        AssertSqlFails(dataSource, "DELETE FROM tool_calls WHERE run_id = @id", s.RunId, "55000");
        using var apiSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        AssertSqlFails(apiSource, "UPDATE tool_calls SET arguments_redacted = '{}' WHERE run_id = @id", s.RunId, "42501");
        AssertSqlFails(apiSource, "UPDATE run_sessions SET token_hash = '\\x00' WHERE run_id = @id", s.RunId, "42501");
        AssertSqlFails(apiSource, "DELETE FROM run_sessions WHERE run_id = @id", s.RunId, "42501");
    }

    [PostgresFact]
    public void Postgres_restricted_role_runs_the_gateway()
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        var s = Start(new PostgresLedger(dataSource));
        Call(s, "wait", new JsonObject { ["days"] = 1 });
        Assert.Equal("committed", Status(Call(s, "pay_bill", new JsonObject { ["bill_id"] = BillId(s, "RENT-01"), ["idempotency_key"] = "r" })));
        Assert.Equal("ok", Status(Call(s, "request_funding", new JsonObject { ["amount_sen"] = 1, ["justification"] = "j" })));
        s.Ledger.TransitionRun(s.RunId, RunStatus.Completed, null, FinanceEvents.WorkerSource);
        Assert.Null(s.Gateway.ResolveSession(s.Token));
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
