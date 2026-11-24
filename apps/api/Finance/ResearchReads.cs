using System.Data;
using System.Text.Json.Nodes;
using Npgsql;

namespace Finmind.Api.Finance;

// ADR-0013 part B: the evidence label of one action (see "Contract details").
public static class ActionEvidence
{
    private static readonly HashSet<string> RejectedOutcomes = ["denied", "malformed", "conflict", "unsupported_capability"];
    private static readonly HashSet<string> AgentStatements = ["submit_plan", "finish", "request_funding"];

    public static string Of(string tool, string? outcome, Guid? journalCommittedByThisCall) =>
        journalCommittedByThisCall is not null ? "committed"
        : outcome is not null && RejectedOutcomes.Contains(outcome) ? "denied"
        : outcome == "ok" && AgentStatements.Contains(tool) ? "reported_by_agent"
        : "proposed";

    public static readonly string[] Labels = ["proposed", "denied", "committed", "reported_by_agent"];
}

// ADR-0013 part B: researcher reads. Each call is one read-only REPEATABLE READ transaction without a run lock,
// so observers never delay an agent and every figure in a response comes from the same snapshot.
public sealed class ResearchReads(NpgsqlDataSource dataSource)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    public static readonly string[] Statuses =
        ["DRAFT", "QUEUED", "INITIALIZING", "RUNNING", "COMPLETED", "FAILED", "CANCELLED", "BUDGET_EXCEEDED"];

    private T Read<T>(Func<NpgsqlConnection, NpgsqlTransaction, T> body)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) readOnly.ExecuteNonQuery();
        var result = body(connection, transaction);
        transaction.Commit();
        return result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private const string RunColumns =
        "r.id, r.created_at, r.status, r.status_reason, r.config_snapshot::text, r.queued_at, r.started_at, r.finished_at, " +
        "r.virtual_day, r.model_tokens_used, r.model_cost_micro_usd, r.model_cost_reserved_micro_usd, " +
        "extract(epoch FROM coalesce(r.finished_at, now()) - r.started_at)::bigint";

    private static JsonObject Summary(NpgsqlDataReader reader)
    {
        var snapshot = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4))!.AsObject();
        var managed = !reader.IsDBNull(2);
        return new JsonObject
        {
            ["runId"] = reader.GetGuid(0),
            ["managed"] = managed,
            // Runs from the older /funding path are development runs, excluded from results (ADR-0008).
            ["researchEligible"] = managed,
            ["status"] = managed ? reader.GetString(2) : null,
            ["statusReason"] = reader.IsDBNull(3) ? null : reader.GetString(3),
            ["scenarioId"] = snapshot?["scenarioId"]?.DeepClone(),
            ["agent"] = snapshot?["agent"] is JsonObject agent
                ? new JsonObject { ["provider"] = agent["provider"]?.DeepClone(), ["modelId"] = agent["modelId"]?.DeepClone() } : null,
            ["createdAt"] = reader.GetFieldValue<DateTimeOffset>(1),
            ["queuedAt"] = Time(reader, 5),
            ["startedAt"] = Time(reader, 6),
            ["finishedAt"] = Time(reader, 7)
        };
    }

    private static JsonNode? Time(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : JsonValue.Create(reader.GetFieldValue<DateTimeOffset>(ordinal));

    // Newest first; "before" is the last run ID of the previous page.
    public JsonObject ListRuns(string? status, bool? managed, int limit, Guid? before) => Read((connection, transaction) =>
    {
        var sql = $"SELECT {RunColumns} FROM experiment_runs r WHERE TRUE";
        var parameters = new List<(string, object)> { ("limit", limit) };
        if (status is not null) { sql += " AND r.status = @status"; parameters.Add(("status", status)); }
        if (managed is { } isManaged) sql += isManaged ? " AND r.status IS NOT NULL" : " AND r.status IS NULL";
        if (before is { } cursor)
        {
            sql += " AND (r.created_at, r.id) < (SELECT created_at, id FROM experiment_runs WHERE id = @before)";
            parameters.Add(("before", cursor));
        }
        using var command = Command(connection, transaction, sql + " ORDER BY r.created_at DESC, r.id DESC LIMIT @limit", parameters.ToArray());
        using var reader = command.ExecuteReader();
        var runs = new JsonArray();
        while (reader.Read()) runs.Add(Summary(reader));
        return new JsonObject { ["runs"] = runs, ["nextBefore"] = runs.Count > 0 ? runs[^1]!["runId"]!.DeepClone() : null };
    });

    public JsonObject? GetRun(Guid runId) => Read((connection, transaction) =>
    {
        JsonObject run;
        long tokens, cost, reserved, elapsed;
        int? day;
        using (var command = Command(connection, transaction, $"SELECT {RunColumns}, r.config_hash FROM experiment_runs r WHERE r.id = @id", ("id", runId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            run = Summary(reader);
            run["configSnapshot"] = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4));
            run["configHash"] = reader.IsDBNull(13) ? null : Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(13));
            day = reader.IsDBNull(8) ? null : reader.GetInt32(8);
            (tokens, cost, reserved) = (reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11));
            elapsed = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
        }

        var actions = Actions(connection, transaction, runId, 0, int.MaxValue);
        var counts = new JsonObject { ["total"] = actions.Count };
        foreach (var label in ActionEvidence.Labels)
            counts[label] = actions.Count(a => (string)a["evidence"]! == label);
        run["actionCounts"] = counts;
        run["virtualDay"] = day;

        if (run["configSnapshot"] is not JsonObject snapshot)
        {
            run["budgets"] = null;
            return run;
        }
        run["durationDays"] = snapshot["durationDays"]?.DeepClone();
        run["budgets"] = new JsonObject
        {
            ["actions"] = new JsonObject { ["used"] = actions.Count, ["max"] = snapshot["maxActions"]?.DeepClone() },
            ["modelTokens"] = new JsonObject { ["used"] = tokens, ["max"] = snapshot["maxModelTokens"]?.DeepClone() },
            // Reserved: worst cases of model calls not yet reported, which count toward the cap (ADR-0013 A1).
            ["costMicroUsd"] = new JsonObject
            {
                ["recorded"] = cost, ["reserved"] = reserved, ["max"] = snapshot["maxCostMicroUsd"]?.DeepClone() ?? 0
            },
            ["wallTimeSeconds"] = new JsonObject { ["elapsed"] = elapsed, ["max"] = snapshot["maxWallTimeSeconds"]?.DeepClone() }
        };
        return run;
    });

    private static bool Exists(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM experiment_runs WHERE id = @id)", ("id", runId));
        return command.ExecuteScalar() is true;
    }

    private static long AsOfSequence(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        using var command = Command(connection, transaction, "SELECT coalesce(max(sequence), 0) FROM run_events WHERE run_id = @id", ("id", runId));
        return (long)command.ExecuteScalar()!;
    }

    public JsonObject? Wallets(Guid runId) => Read((connection, transaction) =>
        Exists(connection, transaction, runId) ? WalletsIn(connection, transaction, runId) : null);

    private static JsonObject WalletsIn(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId)
    {
        var fromEvents = new Dictionary<Guid, long>();
        using (var command = Command(connection, transaction,
            "SELECT (p->>'accountId')::uuid, sum((p->>'amountSen')::bigint)::bigint FROM run_events e, " +
            "jsonb_array_elements(e.payload->'postings') p WHERE e.run_id = @id AND e.event_type = 'LEDGER_COMMITTED' GROUP BY 1",
            ("id", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) fromEvents[reader.GetGuid(0)] = reader.GetInt64(1);

        var accounts = new JsonArray();
        var reconciled = true;
        using (var command = Command(connection, transaction,
            "SELECT a.id, a.kind, coalesce(sum(p.signed_amount_sen), 0)::bigint FROM accounts a " +
            "LEFT JOIN journal_postings p ON p.account_id = a.id AND p.run_id = a.run_id " +
            "WHERE a.run_id = @id AND a.kind IN ('agent_cash', 'agent_savings') GROUP BY a.id, a.kind ORDER BY a.kind, a.id",
            ("id", runId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var balance = reader.GetInt64(2);
                var replayed = fromEvents.GetValueOrDefault(reader.GetGuid(0));
                reconciled &= balance == replayed;
                accounts.Add(new JsonObject
                {
                    ["accountId"] = reader.GetGuid(0),
                    ["kind"] = reader.GetString(1) == "agent_cash" ? "cash" : "savings",
                    ["balanceSen"] = balance,
                    ["eventBalanceSen"] = replayed,
                    ["currency"] = "MYR",
                    ["evidence"] = "verified_by_ledger"
                });
            }
        }
        return new JsonObject
        {
            ["runId"] = runId, ["asOfSequence"] = AsOfSequence(connection, transaction, runId),
            ["reconciled"] = reconciled, ["accounts"] = accounts
        };
    }

    // ADR-0015: finmind-metrics/v1, from the same snapshot as everything it is derived from.
    public JsonObject? Metrics(Guid runId) => Read((connection, transaction) =>
    {
        JsonObject? snapshot;
        long tokens, cost, reserved, elapsed;
        bool managed;
        using (var command = Command(connection, transaction,
            "SELECT config_snapshot::text, model_tokens_used, model_cost_micro_usd, model_cost_reserved_micro_usd, " +
            "extract(epoch FROM coalesce(finished_at, now()) - started_at)::bigint, status IS NOT NULL FROM experiment_runs WHERE id = @id",
            ("id", runId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            snapshot = reader.IsDBNull(0) ? null : JsonNode.Parse(reader.GetString(0))!.AsObject();
            (tokens, cost, reserved) = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            elapsed = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            managed = reader.GetBoolean(5);
        }

        var kinds = new Dictionary<Guid, string>();
        using (var command = Command(connection, transaction, "SELECT id, kind FROM accounts WHERE run_id = @id", ("id", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) kinds[reader.GetGuid(0)] = reader.GetString(1);

        var journals = new Dictionary<Guid, (string Kind, long? Sequence, Guid? Reverses, Guid? Obligation, List<MetricPosting> Postings)>();
        using (var command = Command(connection, transaction,
            "SELECT j.id, j.kind, (SELECT min(e.sequence) FROM run_events e WHERE e.run_id = j.run_id " +
            "AND e.event_type = 'LEDGER_COMMITTED' AND e.journal_id = j.id), j.reverses_entry_id, j.obligation_id " +
            "FROM journal_entries j WHERE j.run_id = @id", ("id", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                journals[reader.GetGuid(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), []);
        using (var command = Command(connection, transaction,
            "SELECT entry_id, account_id, signed_amount_sen FROM journal_postings WHERE run_id = @id ORDER BY id", ("id", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) journals[reader.GetGuid(0)].Postings.Add(new MetricPosting(reader.GetGuid(1), reader.GetInt64(2)));

        int turns;
        long latency;
        using (var command = Command(connection, transaction,
            "SELECT count(*)::int, coalesce(sum((payload->>'latencyMs')::bigint), 0)::bigint FROM run_events " +
            "WHERE run_id = @id AND event_type = 'MODEL_INVOCATION_COMPLETED'", ("id", runId)))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            (turns, latency) = (reader.GetInt32(0), reader.GetInt64(1));
        }
        bool chainIntact;
        using (var command = Command(connection, transaction, "SELECT finmind_verify_run_events(@id) IS NULL", ("id", runId)))
            chainIntact = command.ExecuteScalar() is true;

        var actions = Actions(connection, transaction, runId, 0, int.MaxValue).Select(a => new MetricAction(
            (string)a["toolCallId"]!, (string)a["tool"]!, (string?)a["outcome"], (string?)a["reasonCode"], a["arguments"],
            (long)a["proposedSequence"]!)).ToList();
        var opening = journals.Values.FirstOrDefault(j => j.Kind == "opening_funding").Postings?.Where(p => p.AmountSen > 0).Sum(p => p.AmountSen) ?? 0;
        long? Number(string key) => snapshot?[key] is JsonValue value && value.TryGetValue<long>(out var n) ? n : null;

        var input = new MetricInput(
            Number("startingBalanceSen") ?? opening, kinds,
            journals.Select(j => new MetricJournal(j.Key, j.Value.Kind, j.Value.Sequence, j.Value.Reverses, j.Value.Obligation, j.Value.Postings)).ToList(),
            actions, turns, latency, (int?)Number("maxActions"), tokens, Number("maxModelTokens"), cost, reserved, Number("maxCostMicroUsd"),
            elapsed, Number("maxWallTimeSeconds"), (bool)WalletsIn(connection, transaction, runId)["reconciled"]!, chainIntact,
            AsOfSequence(connection, transaction, runId));
        var metrics = MetricsV1.Compute(input);
        metrics["runId"] = runId;
        metrics["researchEligible"] = managed;
        return metrics;
    });

    // Ordered by the LEDGER_COMMITTED sequence; journals from before the event log come first.
    public JsonObject? Ledger(Guid runId, long afterSequence, int limit) => Read((connection, transaction) =>
    {
        if (!Exists(connection, transaction, runId)) return null;
        var journals = new List<JsonObject>();
        var byId = new Dictionary<Guid, JsonObject>();
        using (var command = Command(connection, transaction,
            "SELECT * FROM (SELECT j.id, j.kind, j.description, j.idempotency_key, j.created_at, j.obligation_id, " +
            "j.reverses_entry_id, j.reversal_reason, j.initiator_type, " +
            "(SELECT min(e.sequence) FROM run_events e WHERE e.run_id = j.run_id AND e.event_type = 'LEDGER_COMMITTED' AND e.journal_id = j.id) AS seq, " +
            "(SELECT r.id FROM journal_entries r WHERE r.reverses_entry_id = j.id) AS reversed_by " +
            "FROM journal_entries j WHERE j.run_id = @id) x " +
            "WHERE (x.seq IS NULL AND @after = 0) OR x.seq > @after ORDER BY x.seq NULLS FIRST, x.created_at, x.id",
            ("id", runId), ("after", afterSequence)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sequenced = !reader.IsDBNull(9);
                if (sequenced && journals.Count(j => j["commitSequence"] is not null) >= limit) break;
                var journal = new JsonObject
                {
                    ["journalId"] = reader.GetGuid(0),
                    ["kind"] = reader.GetString(1),
                    ["description"] = reader.GetString(2),
                    ["idempotencyKey"] = reader.GetString(3),
                    ["createdAt"] = reader.GetFieldValue<DateTimeOffset>(4),
                    ["obligationId"] = reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    ["reversesJournalId"] = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    ["reversalReason"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ["initiator"] = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ["commitSequence"] = sequenced ? reader.GetInt64(9) : null,
                    ["reversedByJournalId"] = reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    ["effective"] = reader.IsDBNull(10),
                    ["evidence"] = "verified_by_ledger",
                    ["postings"] = new JsonArray()
                };
                journals.Add(journal);
                byId[reader.GetGuid(0)] = journal;
            }
        }
        if (byId.Count > 0)
        {
            using var command = Command(connection, transaction,
                "SELECT p.entry_id, p.account_id, a.kind, a.label, p.signed_amount_sen FROM journal_postings p " +
                "JOIN accounts a ON a.id = p.account_id WHERE p.run_id = @id AND p.entry_id = ANY(@ids) ORDER BY p.id",
                ("id", runId), ("ids", byId.Keys.ToArray()));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                byId[reader.GetGuid(0)]["postings"]!.AsArray().Add(new JsonObject
                {
                    ["accountId"] = reader.GetGuid(1), ["accountKind"] = reader.GetString(2),
                    ["label"] = reader.IsDBNull(3) ? null : reader.GetString(3), ["amountSen"] = reader.GetInt64(4)
                });
        }
        var last = journals.LastOrDefault(j => j["commitSequence"] is not null)?["commitSequence"]?.GetValue<long>();
        return new JsonObject
        {
            ["runId"] = runId, ["asOfSequence"] = AsOfSequence(connection, transaction, runId),
            ["journals"] = new JsonArray(journals.Select(j => (JsonNode)j).ToArray()),
            ["nextAfterSequence"] = last ?? afterSequence
        };
    });

    public JsonObject? ListActions(Guid runId, long afterSequence, int limit) => Read((connection, transaction) =>
    {
        if (!Exists(connection, transaction, runId)) return null;
        var actions = Actions(connection, transaction, runId, afterSequence, limit);
        return new JsonObject
        {
            ["runId"] = runId,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
            ["nextAfterSequence"] = actions.Count > 0 ? actions[^1]["proposedSequence"]!.DeepClone() : afterSequence
        };
    });

    // Ordered by TOOL_CALL_PROPOSED sequence, so the cursor cannot skip a call that commits late.
    private static List<JsonObject> Actions(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId,
        long afterSequence, int limit)
    {
        using var command = Command(connection, transaction,
            "SELECT t.tool_call_id, t.tool_name, t.arguments_redacted::text, t.outcome, t.reason_code, p.sequence, " +
            "t.response::text, " +
            "(SELECT r.sequence FROM run_events r WHERE r.run_id = t.run_id AND r.event_type = 'TOOL_RETURNED' AND r.trace_id = t.tool_call_id), " +
            "(SELECT c.journal_id FROM run_events c WHERE c.run_id = t.run_id AND c.event_type = 'LEDGER_COMMITTED' " +
            " AND c.trace_id = t.tool_call_id ORDER BY c.sequence LIMIT 1), t.created_at " +
            "FROM tool_calls t JOIN run_events p ON p.run_id = t.run_id AND p.event_type = 'TOOL_CALL_PROPOSED' AND p.trace_id = t.tool_call_id " +
            "WHERE t.run_id = @id AND p.sequence > @after ORDER BY p.sequence LIMIT @limit",
            ("id", runId), ("after", afterSequence), ("limit", limit));
        using var reader = command.ExecuteReader();
        var actions = new List<JsonObject>();
        while (reader.Read())
        {
            var tool = reader.GetString(1);
            var outcome = reader.IsDBNull(3) ? null : reader.GetString(3);
            Guid? committed = reader.IsDBNull(8) ? null : reader.GetGuid(8);
            actions.Add(new JsonObject
            {
                ["toolCallId"] = reader.GetString(0),
                ["tool"] = tool,
                ["arguments"] = JsonNode.Parse(reader.GetString(2)),
                ["outcome"] = outcome,
                ["reasonCode"] = reader.IsDBNull(4) ? null : reader.GetString(4),
                ["journalId"] = committed,
                ["evidence"] = ActionEvidence.Of(tool, outcome, committed),
                // ADR-0014: what the agent received, as stored (already redacted). Its observation for the next turn.
                ["response"] = reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)),
                ["complete"] = !reader.IsDBNull(7),
                ["proposedSequence"] = reader.GetInt64(5),
                ["returnedSequence"] = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                ["proposedAt"] = reader.GetFieldValue<DateTimeOffset>(9)
            });
        }
        return actions;
    }

    // For the event stream: whether the run is managed, and its status. Null when the run does not exist.
    public (bool Managed, string? Status) Status(Guid runId) => Read((connection, transaction) =>
    {
        using var command = Command(connection, transaction, "SELECT status FROM experiment_runs WHERE id = @id", ("id", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new LedgerValidationException(LedgerReasonCode.RunNotFound, "Run does not exist.");
        return reader.IsDBNull(0) ? (false, (string?)null) : (true, reader.GetString(0));
    });

    public bool RunExists(Guid runId) => Read((connection, transaction) => Exists(connection, transaction, runId));
}
