using Npgsql;

namespace Finmind.Api.Finance;

// ADR-0017 step 2: every export request is recorded before anything is served. If recording fails, nothing is served.
public sealed record ExportLogEntry(Guid RunId, string Format, string Outcome, DateTimeOffset At);

public sealed class ExportLog(NpgsqlDataSource dataSource)
{
    public void Record(Guid runId, string? format, string outcome)
    {
        using var connection = dataSource.OpenConnection();
        using var insert = new NpgsqlCommand(
            "INSERT INTO export_log (id, run_id, format, outcome) VALUES (@id, @run, @format, @outcome)", connection);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("format", string.IsNullOrEmpty(format) ? "(none)" : TextLimits.Cut(format, 20));
        insert.Parameters.AddWithValue("outcome", outcome);
        insert.ExecuteNonQuery();
    }

    public IReadOnlyList<ExportLogEntry> Latest(int limit)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "SELECT run_id, format, outcome, created_at FROM export_log ORDER BY seq DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var entries = new List<ExportLogEntry>();
        while (reader.Read())
            entries.Add(new ExportLogEntry(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return entries;
    }
}
