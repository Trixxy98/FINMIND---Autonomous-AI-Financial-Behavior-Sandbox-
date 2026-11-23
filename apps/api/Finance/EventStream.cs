using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Finmind.Api.Finance;

// ADR-0013 B2: server-sent events for one run. Each poll reads on its own connection and releases it, so a
// watching researcher holds no database connection or transaction while waiting, and never delays the agent.
public static class EventStream
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    // A usage report can follow a terminal status (ADR-0013 A1), so the stream ends only after a quiet period.
    public static readonly TimeSpan QuietBeforeEnd = TimeSpan.FromSeconds(5);
    private const int BatchSize = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // JSON never contains a raw newline, so the data line cannot split the message.
    public static string Format(RunEvent evt) =>
        $"id: {evt.Sequence.ToString(CultureInfo.InvariantCulture)}\nevent: {evt.EventType}\ndata: {JsonSerializer.Serialize(evt, Json)}\n\n";

    public static string End(string? status, long lastSequence) =>
        $"event: end\ndata: {JsonSerializer.Serialize(new { status, lastSequence }, Json)}\n\n";

    // Last-Event-ID (a reconnect) wins over afterSequence. Null when the cursor is invalid.
    public static long? Cursor(string? lastEventId, string? afterSequence)
    {
        var raw = !string.IsNullOrWhiteSpace(lastEventId) ? lastEventId : afterSequence;
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        return long.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor) ? cursor : null;
    }

    public static async Task Run(HttpContext context, Guid runId, long after, PostgresLedger ledger, ResearchReads reads)
    {
        var abort = context.RequestAborted;
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var lastWrite = DateTimeOffset.UtcNow;
        var quietSince = DateTimeOffset.UtcNow;
        try
        {
            while (!abort.IsCancellationRequested)
            {
                // Status first: every event committed before a terminal status is then visible to the read below.
                var (managed, status) = reads.Status(runId);
                var batch = ledger.GetEvents(runId, after, BatchSize);
                if (batch.Count > 0)
                {
                    foreach (var evt in batch) await context.Response.WriteAsync(Format(evt), abort);
                    await context.Response.Body.FlushAsync(abort);
                    after = batch[^1].Sequence;
                    quietSince = batch[^1].OccurredAt;
                    lastWrite = DateTimeOffset.UtcNow;
                    if (batch.Count == BatchSize) continue;
                }

                var terminal = status is { } wire && AgentGatewayRules.IsTerminal(RunLifecycle.Parse(wire));
                if (!managed || (terminal && DateTimeOffset.UtcNow - quietSince >= QuietBeforeEnd))
                {
                    await context.Response.WriteAsync(End(status, after), abort);
                    await context.Response.Body.FlushAsync(abort);
                    return;
                }
                if (DateTimeOffset.UtcNow - lastWrite >= HeartbeatInterval)
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", abort);
                    await context.Response.Body.FlushAsync(abort);
                    lastWrite = DateTimeOffset.UtcNow;
                }
                await Task.Delay(PollInterval, abort);
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            // The client went away; it resumes with Last-Event-ID.
        }
    }
}
