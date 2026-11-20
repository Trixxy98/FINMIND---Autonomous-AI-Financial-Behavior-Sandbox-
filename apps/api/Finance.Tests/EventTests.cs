using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0007: finance decisions and state transitions are recorded in order, identically by both ledgers.
public sealed class EventTests
{
    private sealed record FlowResult(Guid RunId, Guid PaymentJournalId, Guid ReversalJournalId);

    // One run touching every Phase 1 event type. Expected events are listed beside each step.
    private static FlowResult RunFlow(IFinanceLedger ledger)
    {
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;      // RUN_CREATED, ACCOUNT_CREATED x2, LEDGER_COMMITTED
        ledger.OpenRun(runId, "open", 1_000, 500);                                 // LEDGER_REPLAYED
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);          // ACCOUNT_CREATED
        var savings = ledger.CreateAccount(runId, AccountKind.AgentSavings);       // ACCOUNT_CREATED
        var obligation = ledger.CreateObligation(runId, merchant, 200, "INV-1");   // OBLIGATION_CREATED
        ledger.CreateObligation(runId, merchant, 200, "INV-1");                    // (no event: identical replay)
        var payment = ledger.PayObligation(runId, "pay-1", cash, obligation.ObligationId); // LEDGER_COMMITTED, OBLIGATION_PAID
        Assert.ThrowsAny<LedgerException>(() => ledger.Transfer(runId, "t-1", cash, merchant, 600)); // POLICY_DENIED
        ledger.Transfer(runId, "t-2", cash, merchant, 100);                        // LEDGER_COMMITTED
        ledger.Transfer(runId, "t-2", cash, merchant, 100);                        // LEDGER_REPLAYED
        ledger.MoveToSavings(runId, "s-1", cash, savings, 50);                     // LEDGER_COMMITTED
        var reversal = ledger.ReverseJournal(runId, "rev-1", payment.JournalId, "payment bounced",
            ReversalInitiator.ScenarioEngine);                                     // LEDGER_COMMITTED, JOURNAL_REVERSED, OBLIGATION_PAYMENT_REVERSED
        ledger.PayObligation(runId, "pay-1", cash, obligation.ObligationId);       // LEDGER_REPLAYED (not effective)
        Assert.ThrowsAny<LedgerException>(() =>
            ledger.CreateObligation(runId, merchant, 999, "INV-1"));               // POLICY_DENIED (conflict)
        return new FlowResult(runId, payment.JournalId, reversal.JournalId);
    }

    private static readonly (string Type, string Operation, string? Outcome, string? ReasonCode, string Source)[] ExpectedFlow =
    [
        ("RUN_CREATED", "open_run", null, null, "internal_api"),
        ("ACCOUNT_CREATED", "open_run", null, null, "internal_api"),
        ("ACCOUNT_CREATED", "open_run", null, null, "internal_api"),
        ("LEDGER_COMMITTED", "open_run", "committed", null, "internal_api"),
        ("LEDGER_REPLAYED", "open_run", "replayed", null, "internal_api"),
        ("ACCOUNT_CREATED", "create_account", null, null, "internal_api"),
        ("ACCOUNT_CREATED", "create_account", null, null, "internal_api"),
        ("OBLIGATION_CREATED", "create_obligation", null, null, "internal_api"),
        ("LEDGER_COMMITTED", "pay_obligation", "committed", null, "internal_api"),
        ("OBLIGATION_PAID", "pay_obligation", null, null, "internal_api"),
        ("POLICY_DENIED", "transfer", "denied", "EXCEEDS_TRANSACTION_LIMIT", "internal_api"),
        ("LEDGER_COMMITTED", "transfer", "committed", null, "internal_api"),
        ("LEDGER_REPLAYED", "transfer", "replayed", null, "internal_api"),
        ("LEDGER_COMMITTED", "move_to_savings", "committed", null, "internal_api"),
        ("LEDGER_COMMITTED", "reverse_journal", "committed", null, "scenario_engine"),
        ("JOURNAL_REVERSED", "reverse_journal", null, null, "scenario_engine"),
        ("OBLIGATION_PAYMENT_REVERSED", "reverse_journal", null, null, "scenario_engine"),
        ("LEDGER_REPLAYED", "pay_obligation", "replayed", null, "internal_api"),
        ("POLICY_DENIED", "create_obligation", "conflict", "INVOICE_REFERENCE_CONFLICT", "internal_api"),
    ];

    [Fact]
    public void InMemory_flow_records_documented_events() => AssertFlow(new InMemoryLedger());

    [PostgresFact]
    public void Postgres_flow_records_documented_events()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var flow = AssertFlow(ledger);

        var events = ledger.GetEvents(flow.RunId, 0, 500);
        Assert.All(events, e => Assert.Matches("^[0-9a-f]{64}$", e.EventHash!));
        Assert.Null(VerifyChain(dataSource, flow.RunId));
    }

    private static FlowResult AssertFlow(IFinanceLedger ledger)
    {
        var flow = RunFlow(ledger);
        var events = ledger.GetEvents(flow.RunId, 0, 500);

        Assert.Equal(ExpectedFlow, events.Select(e => (e.EventType, e.Operation!, e.Outcome, e.ReasonCode, e.Source)).ToArray());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(1, e.EventVersion));

        // Committed events match journals one-to-one, with the same postings.
        var journals = ledger.GetJournals(flow.RunId);
        var committed = events.Where(e => e.EventType == "LEDGER_COMMITTED").ToArray();
        Assert.Equal(journals.Select(j => j.JournalId).Order(), committed.Select(e => e.JournalId!.Value).Order());
        foreach (var journal in journals)
        {
            var payloadPostings = committed.Single(e => e.JournalId == journal.JournalId).Payload["postings"]!.AsArray()
                .Select(p => (p!["accountId"]!.GetValue<Guid>(), p["amountSen"]!.GetValue<long>()));
            Assert.Equal(journal.Postings.Select(p => (p.AccountId, p.AmountSen)), payloadPostings);
        }

        var denial = events.First(e => e.EventType == "POLICY_DENIED");
        Assert.Equal(600, denial.Payload["request"]!["amountSen"]!.GetValue<long>());
        Assert.Equal("t-1", denial.IdempotencyKey);
        Assert.Null(denial.JournalId);

        var lastReplay = events.Last(e => e.EventType == "LEDGER_REPLAYED");
        Assert.Equal(flow.PaymentJournalId, lastReplay.JournalId);
        Assert.False(lastReplay.Payload["effective"]!.GetValue<bool>());
        Assert.Equal(flow.ReversalJournalId, lastReplay.Payload["reversedByJournalId"]!.GetValue<Guid>());

        var reversed = events.Single(e => e.EventType == "JOURNAL_REVERSED");
        Assert.Equal("payment bounced", reversed.Payload["reason"]!.GetValue<string>());
        Assert.Equal("scenario_engine", reversed.Payload["initiator"]!.GetValue<string>());

        Assert.All(events, e => AssertNoDiagnosticText(e.Payload));
        Assert.Equal(3, ledger.GetEvents(flow.RunId, 16, 500).Count);
        Assert.Equal(2, ledger.GetEvents(flow.RunId, 0, 2).Count);
        return flow;
    }

    [Fact]
    public void InMemory_unknown_or_empty_run_records_nothing() => AssertUnknownRunRecordsNothing(new InMemoryLedger());

    [PostgresFact]
    public void Postgres_unknown_or_empty_run_records_nothing()
    {
        using var dataSource = OwnerDataSource();
        AssertUnknownRunRecordsNothing(new PostgresLedger(dataSource));
    }

    private static void AssertUnknownRunRecordsNothing(IFinanceLedger ledger)
    {
        var unknownRun = Guid.NewGuid();
        Assert.ThrowsAny<LedgerException>(() => ledger.Transfer(unknownRun, "k", Guid.NewGuid(), Guid.NewGuid(), 10));
        Assert.ThrowsAny<LedgerException>(() => ledger.CreateAccount(Guid.Empty, AccountKind.Merchant));
        Assert.Empty(ledger.GetEvents(unknownRun, 0, 500));
        Assert.Empty(ledger.GetEvents(Guid.Empty, 0, 500));
    }

    [PostgresFact]
    public async Task Postgres_concurrent_operations_keep_a_gap_free_valid_chain()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            try { ledger.Transfer(runId, $"t-{index % 6}", cash, merchant, 150); }
            catch (LedgerException) { }
        })));

        var events = ledger.GetEvents(runId, 0, 500);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(ledger.GetJournals(runId).Count, events.Count(e => e.EventType == "LEDGER_COMMITTED"));
        Assert.Null(VerifyChain(dataSource, runId));
    }

    [PostgresFact]
    public void Postgres_verification_detects_a_changed_payload()
    {
        using var dataSource = OwnerDataSource();
        var flow = RunFlow(new PostgresLedger(dataSource));

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        // Bypass the append-only trigger for this session only, then roll back.
        Execute(connection, transaction, "SET LOCAL session_replication_role = replica");
        using (var tamper = new NpgsqlCommand(
            "UPDATE run_events SET payload = jsonb_set(payload, '{request,amountSen}', '1') " +
            "WHERE run_id = @run_id AND sequence = 11", connection, transaction))
        {
            tamper.Parameters.AddWithValue("run_id", flow.RunId);
            Assert.Equal(1, tamper.ExecuteNonQuery());
        }

        using var verify = new NpgsqlCommand("SELECT finmind_verify_run_events(@run_id)", connection, transaction);
        verify.Parameters.AddWithValue("run_id", flow.RunId);
        Assert.Equal(11L, verify.ExecuteScalar());
        transaction.Rollback();

        Assert.Null(VerifyChain(dataSource, flow.RunId));
    }

    [PostgresFact]
    public void Postgres_database_assigns_sequence_and_hash()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        ledger.OpenRun(runId, "open", 1_000, 500);

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var insert = new NpgsqlCommand(
            "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, payload, prev_hash, event_hash) " +
            "VALUES (gen_random_uuid(), @run_id, 999, 'FINANCE_ERROR', 1, '2000-01-01', 'internal_api', '{}', '\\x01', '\\x02') " +
            "RETURNING sequence, occurred_at > '2000-01-02', length(event_hash), finmind_verify_run_events(@run_id)",
            connection, transaction);
        insert.Parameters.AddWithValue("run_id", runId);
        using (var reader = insert.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(5L, reader.GetInt64(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(32, reader.GetInt32(2));
        }
        transaction.Rollback();
    }

    [PostgresFact]
    public void Postgres_events_roll_back_with_a_failed_ledger_transaction()
    {
        using var dataSource = OwnerDataSource();
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;
        var before = ledger.GetEvents(runId, 0, 500).Count;

        using (var connection = dataSource.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var journalId = Guid.NewGuid();
            using var journal = new NpgsqlCommand(
                "INSERT INTO journal_entries (id, run_id, kind, idempotency_key, description) " +
                "VALUES (@id, @run_id, 'transfer', 'broken', 'unbalanced')", connection, transaction);
            journal.Parameters.AddWithValue("id", journalId);
            journal.Parameters.AddWithValue("run_id", runId);
            journal.ExecuteNonQuery();
            using var posting = new NpgsqlCommand(
                "INSERT INTO journal_postings (entry_id, run_id, account_id, currency, signed_amount_sen) " +
                "VALUES (@id, @run_id, @cash, 'MYR', -5)", connection, transaction);
            posting.Parameters.AddWithValue("id", journalId);
            posting.Parameters.AddWithValue("run_id", runId);
            posting.Parameters.AddWithValue("cash", cash);
            posting.ExecuteNonQuery();
            using var evt = new NpgsqlCommand(
                "INSERT INTO run_events (id, run_id, sequence, event_type, event_version, occurred_at, source, payload, event_hash) " +
                "VALUES (gen_random_uuid(), @run_id, 0, 'LEDGER_COMMITTED', 1, now(), 'internal_api', '{}', '\\x00')",
                connection, transaction);
            evt.Parameters.AddWithValue("run_id", runId);
            evt.ExecuteNonQuery();

            Assert.Equal("23514", Assert.Throws<PostgresException>(() => transaction.Commit()).SqlState);
        }

        Assert.Equal(before, ledger.GetEvents(runId, 0, 500).Count);
        Assert.Null(VerifyChain(dataSource, runId));
    }

    [PostgresFact]
    public void Postgres_api_role_cannot_change_or_delete_events()
    {
        var apiConnectionString = Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")
            ?? throw new InvalidOperationException("FINMIND_API_TEST_DATABASE is required.");
        using var dataSource = NpgsqlDataSource.Create(apiConnectionString);
        var ledger = new PostgresLedger(dataSource);
        var runId = Guid.NewGuid();
        ledger.OpenRun(runId, "open", 1_000, 500);
        Assert.Equal(4, ledger.GetEvents(runId, 0, 500).Count);

        foreach (var sql in new[]
                 {
                     "UPDATE run_events SET source = 'agent' WHERE run_id = @run_id",
                     "DELETE FROM run_events WHERE run_id = @run_id",
                     "TRUNCATE run_events"
                 })
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("run_id", runId);
            Assert.Equal("42501", Assert.Throws<PostgresException>(() => command.ExecuteNonQuery()).SqlState);
        }

        Assert.Equal(4, ledger.GetEvents(runId, 0, 500).Count);
    }

    private static void AssertNoDiagnosticText(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                Assert.DoesNotContain("message", obj.Select(p => p.Key));
                foreach (var (_, child) in obj) AssertNoDiagnosticText(child);
                break;
            case JsonArray array:
                foreach (var child in array) AssertNoDiagnosticText(child);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                Assert.DoesNotContain("exceeded", text);
                Assert.DoesNotContain("Password", text);
                break;
        }
    }

    private static NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);

    private static long? VerifyChain(NpgsqlDataSource dataSource, Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT finmind_verify_run_events(@run_id)", connection);
        command.Parameters.AddWithValue("run_id", runId);
        return command.ExecuteScalar() is long sequence ? sequence : null;
    }

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }
}
