using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0015 step 4: export formats.
public static class RunExport
{
    public const string Version = "finmind-export/v2"; // v2 (ADR-0018): adds the outcomes record

    public const string RedactionNote =
        "Agent arguments and text are exported as stored, and were redacted when recorded (ADR-0009): control characters " +
        "removed, credential-like strings replaced, each string cut at 2,000 characters on a code point boundary. " +
        "Invalid Unicode was replaced with U+FFFD (ADR-0013). Tokens and provider keys are never stored.";

    public const string HashNote =
        "Each event carries hashInput, the exact text whose SHA-256 is eventHash, and prevHash, the previous eventHash. " +
        "finmind-event-v1 (ADR-0007) covers a subset of fields; finmind-event-v2 (ADR-0013) covers every stored field. " +
        "Verify with sha256(utf8(hashInput)) == eventHash and the prevHash links.";

    // The same text the database hashes (migrations 006 and 014). ExportTests checks it reproduces every stored hash.
    public const string HashInputSql =
        "CASE hash_version WHEN 2 THEN 'finmind-event-v2' || E'\\n' || jsonb_build_object(" +
        "'id', id, 'run_id', run_id, 'sequence', sequence, 'event_type', event_type, 'event_version', event_version, " +
        "'occurred_at', to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'), " +
        "'virtual_time', to_char(virtual_time AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'), " +
        "'source', source, 'trace_id', trace_id, 'operation', operation, 'outcome', outcome, 'reason_code', reason_code, " +
        "'idempotency_key', idempotency_key, 'journal_id', journal_id, 'obligation_id', obligation_id, 'payload', payload, " +
        "'prev_hash', encode(prev_hash, 'hex'), 'hash_version', hash_version)::text " +
        "ELSE 'finmind-event-v1' || E'\\n' || coalesce(encode(prev_hash, 'hex'), '') || E'\\n' || run_id::text || E'\\n' || " +
        "sequence::text || E'\\n' || event_type || E'\\n' || event_version::text || E'\\n' || " +
        "to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') || E'\\n' || source || E'\\n' || payload::text END";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Jsonl(IEnumerable<JsonObject> records)
    {
        var text = new StringBuilder();
        foreach (var record in records) text.Append(record.ToJsonString(Json)).Append('\n');
        return text.ToString();
    }

    // A text cell is quoted when needed, and never starts like a spreadsheet formula.
    public static string CsvText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string CsvNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string PostingsCsv(IEnumerable<JsonObject> records)
    {
        var text = new StringBuilder(
            "commit_sequence,journal_id,journal_kind,created_at,effective,reversed_by_journal_id,reverses_journal_id," +
            "account_id,account_kind,account_label,amount_sen\n");
        foreach (var journal in records.Where(r => (string)r["type"]! == "journal"))
        {
            foreach (var posting in journal["postings"]!.AsArray())
            {
                text.Append(journal["commitSequence"] is JsonValue seq ? CsvNumber(seq.GetValue<long>()) : "").Append(',')
                    .Append(journal["journalId"]).Append(',')
                    .Append(CsvText((string?)journal["kind"])).Append(',')
                    .Append(journal["createdAt"]!.GetValue<DateTimeOffset>().ToString("O", CultureInfo.InvariantCulture)).Append(',')
                    .Append((bool)journal["effective"]! ? "true" : "false").Append(',')
                    .Append(journal["reversedByJournalId"]?.ToString() ?? "").Append(',')
                    .Append(journal["reversesJournalId"]?.ToString() ?? "").Append(',')
                    .Append(posting!["accountId"]).Append(',')
                    .Append(CsvText((string?)posting["accountKind"])).Append(',')
                    .Append(CsvText((string?)posting["label"])).Append(',')
                    .Append(CsvNumber((long)posting["amountSen"]!)).Append('\n');
            }
        }
        return text.ToString();
    }
}
