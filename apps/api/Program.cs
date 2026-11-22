using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Finmind")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Finmind before starting the API.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresLedger>();
builder.Services.AddSingleton(sp => new ToolGateway(sp.GetRequiredService<PostgresLedger>(), TimeProvider.System));
var app = builder.Build();

// ADR-0010: invalid scenario files stop startup.
var scenarios = ScenarioCatalog.All;
app.Logger.LogInformation("Loaded {Count} scenario files.", scenarios.Count);
// ADR-0012: the agent prompt must exist at startup; its hash is recorded in new Claude runs.
app.Logger.LogInformation("Agent prompt {Version} loaded.", PromptCatalog.Current.Version);

// Every finance endpoint returns ADR-0005 bodies; internal errors are logged server-side only.
IResult Execute(Func<IResult> action, bool isRead = false)
{
    try
    {
        return action();
    }
    catch (Exception ex)
    {
        var (statusCode, body) = isRead ? LedgerHttpResponses.ForReadException(ex) : LedgerHttpResponses.ForException(ex);
        if (statusCode >= 500)
            app.Logger.LogError(ex, "Finance request failed with {ReasonCode}.", body.ReasonCode);
        return Results.Json(body, statusCode: statusCode);
    }
}

IResult Error(LedgerReasonCode code, string message)
{
    var (statusCode, body) = LedgerHttpResponses.ForError(code, message);
    return Results.Json(body, statusCode: statusCode);
}

IResult CommitResponse(CommitResult result) => Results.Ok(new
{
    status = LedgerHttpResponses.StatusFor(result),
    reasonCode = (string?)null,
    result.Committed,
    result.JournalId,
    result.WasIdempotentReplay,
    result.Effective,
    result.ReversedByJournalId
});

app.MapGet("/health", (NpgsqlDataSource dataSource) =>
{
    try
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT 1", connection);
        command.ExecuteScalar();
        return Results.Ok(new { status = "ok", service = "finmind-api", phase = 1, database = "ok" });
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "unavailable", service = "finmind-api", database = "unavailable" }, statusCode: 503);
    }
});

// ADR-0008: managed runs. Researcher endpoints only start and cancel; runtime transitions use the worker interface (ADR-0009).
var codeRevision = builder.Configuration["FINMIND_CODE_REVISION"];

app.MapPost("/internal/runs", (JsonObject? config, PostgresLedger ledger) =>
    Execute(() =>
    {
        var run = ledger.CreateManagedRun(config, codeRevision);
        return Results.Created($"/internal/runs/{run.RunId}", run);
    }));

app.MapGet("/internal/scenarios", () => Results.Ok(ScenarioCatalog.All.Values
    .OrderBy(s => s.Id, StringComparer.Ordinal).ThenBy(s => s.Version)
    .Select(s => new
    {
        s.Id, s.Version, versionLabel = s.VersionLabel, s.Title, s.RecommendedObjective, fileHash = s.FileHash,
        latest = ScenarioCatalog.Latest(s.Id)!.Version == s.Version,
        merchantCount = s.Merchants.Count, eventCount = s.Events.Count
    })));

app.MapGet("/internal/runs/{runId:guid}/environment", (Guid runId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.GetEnvironment(runId)), isRead: true));

app.MapGet("/internal/runs/{runId:guid}", (Guid runId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.GetRun(runId)), isRead: true));

app.MapPost("/internal/runs/{runId:guid}/start", (Guid runId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.TransitionRun(runId, RunStatus.Queued, null, "internal_api"))));

app.MapPost("/internal/runs/{runId:guid}/cancel", (Guid runId, CancelRunRequest request, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.TransitionRun(runId, RunStatus.Cancelled, request.Reason, "internal_api"))));

app.MapPost("/internal/runs/{runId:guid}/accounts", (Guid runId, CreateAccountRequest request, PostgresLedger ledger) =>
{
    if (string.IsNullOrWhiteSpace(request.Kind))
        return Error(LedgerReasonCode.MissingRequiredField, "Account kind is required.");
    if (!Enum.TryParse<AccountKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind))
        return Error(LedgerReasonCode.UnsupportedAccountKind, "Unsupported account kind.");

    return Execute(() =>
    {
        ledger.EnsureDirectWriteAllowed(runId, FinanceEvents.Operations.CreateAccount, FinanceEvents.Request(("kind", kind)));
        var accountId = ledger.CreateAccount(runId, kind);
        return Results.Created($"/internal/runs/{runId}/accounts/{accountId}", new { accountId, runId, kind = kind.ToString(), currency = "MYR" });
    });
});

app.MapGet("/internal/runs/{runId:guid}/accounts/{accountId:guid}/balance", (Guid runId, Guid accountId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(new { runId, accountId, currency = "MYR", balanceSen = ledger.GetBalance(runId, accountId), source = "ledger" }), isRead: true));

app.MapPost("/internal/runs/{runId:guid}/funding", (Guid runId, FundingRequest request, PostgresLedger ledger) =>
    Execute(() =>
    {
        var result = ledger.OpenRun(runId, request.IdempotencyKey, request.AmountSen, request.TransactionLimitSen);
        return Results.Ok(new
        {
            status = LedgerReasonCodes.ToWireValue(result.WasIdempotentReplay ? LedgerOutcome.Replayed : LedgerOutcome.Committed),
            reasonCode = (string?)null,
            result.Committed,
            result.JournalId,
            result.WasIdempotentReplay,
            effective = true,
            reversedByJournalId = (Guid?)null,
            cashAccountId = result.CashAccountId,
            balanceSen = ledger.GetBalance(runId, result.CashAccountId),
            currency = "MYR"
        });
    }));

app.MapPost("/internal/runs/{runId:guid}/transfers", (Guid runId, TransferRequest request, PostgresLedger ledger) =>
    Execute(() =>
    {
        ledger.EnsureDirectWriteAllowed(runId, FinanceEvents.Operations.Transfer, FinanceEvents.Request(
            ("idempotencyKey", request.IdempotencyKey), ("sourceAccountId", request.SourceAccountId),
            ("destinationAccountId", request.DestinationAccountId), ("amountSen", request.AmountSen)));
        return CommitResponse(ledger.Transfer(runId, request.IdempotencyKey, request.SourceAccountId, request.DestinationAccountId, request.AmountSen));
    }));

app.MapPost("/internal/runs/{runId:guid}/savings", (Guid runId, SavingsRequest request, PostgresLedger ledger) =>
    Execute(() =>
    {
        ledger.EnsureDirectWriteAllowed(runId, FinanceEvents.Operations.MoveToSavings, FinanceEvents.Request(
            ("idempotencyKey", request.IdempotencyKey), ("cashAccountId", request.CashAccountId),
            ("savingsAccountId", request.SavingsAccountId), ("amountSen", request.AmountSen)));
        return CommitResponse(ledger.MoveToSavings(runId, request.IdempotencyKey, request.CashAccountId, request.SavingsAccountId, request.AmountSen));
    }));

app.MapPost("/internal/runs/{runId:guid}/bill-payments", (Guid runId, BillPaymentRequest request, PostgresLedger ledger) =>
    Execute(() =>
    {
        ledger.EnsureDirectWriteAllowed(runId, FinanceEvents.Operations.PayObligation, FinanceEvents.Request(
            ("idempotencyKey", request.IdempotencyKey), ("cashAccountId", request.CashAccountId),
            ("obligationId", request.ObligationId)));
        return CommitResponse(ledger.PayObligation(runId, request.IdempotencyKey, request.CashAccountId, request.ObligationId));
    }));

// ADR-0006: researcher-initiated full reversal. The initiator is set here, never read from the body.
// Unauthenticated localhost-only scaffolding; the agent tool gateway must never expose this route.
app.MapPost("/internal/runs/{runId:guid}/journals/{journalId:guid}/reversals", (Guid runId, Guid journalId, ReversalRequest request, PostgresLedger ledger) =>
    Execute(() => CommitResponse(ledger.ReverseJournal(runId, request.IdempotencyKey, journalId, request.Reason, ReversalInitiator.Researcher))));

// Obligation setup never moves money; success keeps returning the stored obligation.
app.MapPost("/internal/runs/{runId:guid}/obligations", (Guid runId, CreateObligationRequest request, PostgresLedger ledger) =>
    Execute(() =>
    {
        ledger.EnsureDirectWriteAllowed(runId, FinanceEvents.Operations.CreateObligation, FinanceEvents.Request(
            ("merchantAccountId", request.MerchantAccountId), ("amountSen", request.AmountSen),
            ("invoiceReference", request.InvoiceReference)));
        return Results.Ok(ledger.CreateObligation(runId, request.MerchantAccountId, request.AmountSen, request.InvoiceReference));
    }));

app.MapGet("/internal/runs/{runId:guid}/obligations/{obligationId:guid}", (Guid runId, Guid obligationId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.GetObligation(runId, obligationId)), isRead: true));

// ADR-0007: ordered, append-only finance events for one run, paged by sequence cursor.
app.MapGet("/internal/runs/{runId:guid}/events/verify", (Guid runId, PostgresLedger ledger) =>
    Execute(() =>
    {
        ledger.GetRun(runId);
        var broken = ledger.VerifyEventChain(runId);
        return Results.Ok(new { runId, intact = broken is null, brokenAtSequence = broken });
    }, isRead: true));

// afterSequence below 0 is treated as 0; limit is clamped to 1..500.
app.MapGet("/internal/runs/{runId:guid}/events", (Guid runId, long? afterSequence, int? limit, PostgresLedger ledger) =>
    Execute(() =>
    {
        var after = Math.Max(afterSequence ?? 0, 0);
        var events = ledger.GetEvents(runId, after, Math.Clamp(limit ?? 100, 1, RunEventPaging.MaxLimit));
        return Results.Ok(new
        {
            runId,
            events,
            nextAfterSequence = events.Count > 0 ? events[^1].Sequence : after
        });
    }, isRead: true));

// ADR-0009 worker interface. Routes exist only when a worker credential of at least 32 characters is configured.
var workerToken = builder.Configuration["FINMIND_WORKER_TOKEN"];
if (workerToken is { Length: >= 32 })
{
    var workerTokenHash = ToolGateway.HashToken(workerToken);
    string? Bearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is var header && header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim() : null;
    bool IsWorker(HttpRequest request) =>
        Bearer(request) is { } presented && CryptographicOperations.FixedTimeEquals(ToolGateway.HashToken(presented), workerTokenHash);

    var worker = app.MapGroup("/internal/worker");

    worker.MapPost("/runs/claim", (HttpRequest request, ToolGateway gateway) =>
    {
        if (!IsWorker(request)) return Results.StatusCode(401);
        return Execute(() =>
        {
            if (gateway.Claim() is not { } claim) return Results.NoContent();
            return Results.Ok(new
            {
                runId = claim.Run.RunId,
                sessionToken = claim.Token,
                configSnapshot = claim.Run.ConfigSnapshot,
                configHash = claim.Run.ConfigHash,
                toolsetVersion = ToolCatalog.Version,
                tools = ToolCatalog.Schemas(),
                unavailableCapabilities = ToolCatalog.UnavailableCapabilities.Order(StringComparer.Ordinal)
            });
        });
    });

    // Session routes: the run is resolved from the session token only.
    IResult WithSession(HttpRequest request, ToolGateway gateway, Func<Guid, IResult> action) =>
        gateway.ResolveSession(Bearer(request)) is { } runId ? action(runId) : Results.StatusCode(401);

    worker.MapPost("/session/initialize", (HttpRequest request, ToolGateway gateway, PostgresLedger ledger) =>
        WithSession(request, gateway, runId => Execute(() =>
        {
            ledger.ActivateRun(runId, FinanceEvents.WorkerSource);
            return Results.Ok(new { runId, status = ledger.GetRun(runId).Status, environment = ledger.GetEnvironment(runId) });
        })));

    worker.MapPost("/session/heartbeat", (HttpRequest request, ToolGateway gateway, PostgresLedger ledger) =>
        WithSession(request, gateway, runId => Execute(() => Results.Ok(new { runId, status = ledger.GetRun(runId).Status }), isRead: true)));

    worker.MapPost("/session/tool-calls", async (HttpRequest request, ToolGateway gateway) =>
    {
        if (gateway.ResolveSession(Bearer(request)) is not { } runId) return Results.StatusCode(401);
        if (request.ContentLength is > 16_384) return Results.StatusCode(413);
        var buffer = new byte[16_385];
        var length = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(length))) > 0)
        {
            length += read;
            if (length > 16_384) return Results.StatusCode(413);
        }

        JsonObject? body;
        try { body = JsonNode.Parse(buffer.AsSpan(0, length))?.AsObject(); }
        catch (Exception) { body = null; }
        if (body is null || body.Any(p => p.Key is not ("toolCallId" or "tool" or "arguments"))
            || (string?)(body["toolCallId"] as JsonValue) is not { } toolCallId || !ToolGateway.IsValidToolCallId(toolCallId)
            || (string?)(body["tool"] as JsonValue) is not { Length: >= 1 and <= 100 } tool)
            return Results.BadRequest(new { error = "Body must be { toolCallId, tool, arguments } with a printable toolCallId of 1-100 characters." });

        return Execute(() => Results.Json(gateway.Execute(runId, toolCallId, tool, body["arguments"]?.DeepClone())));
    });

    worker.MapPost("/session/model-reservations", (HttpRequest request, JsonObject? body, ToolGateway gateway) =>
        WithSession(request, gateway, runId => Execute(() => Results.Json(gateway.ReserveModelCall(runId, body)))));

    // The only route that accepts a revoked or expired session: it can only report usage (ADR-0013 A1).
    worker.MapPost("/session/model-invocations", (HttpRequest request, JsonObject? body, ToolGateway gateway) =>
        gateway.ResolveSessionForUsage(Bearer(request)) is { } runId
            ? Execute(() => Results.Json(gateway.RecordModelInvocation(runId, body)))
            : Results.StatusCode(401));

    worker.MapPost("/session/finish", (HttpRequest request, FinishRunRequest finish, ToolGateway gateway, PostgresLedger ledger) =>
        WithSession(request, gateway, runId => Execute(() =>
        {
            RunStatus? target = finish.Outcome switch
            {
                "completed" => RunStatus.Completed,
                "failed" => RunStatus.Failed,
                "budget_exceeded" => RunStatus.BudgetExceeded,
                _ => null
            };
            if (target is null) return Error(LedgerReasonCode.MissingRequiredField, "Outcome must be completed, failed, or budget_exceeded.");
            return Results.Ok(ledger.TransitionRun(runId, target.Value, finish.Reason, FinanceEvents.WorkerSource));
        })));
}
else
{
    app.Logger.LogWarning("FINMIND_WORKER_TOKEN is missing or shorter than 32 characters; worker routes are disabled.");
}

// ADR-0013 A1: a run whose worker died does not stay RUNNING, even if no worker polls again.
var reaper = app.Services.GetRequiredService<ToolGateway>();
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    try
    {
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            try
            {
                var failed = reaper.ReapExpiredSessions();
                if (failed > 0) app.Logger.LogWarning("Failed {Count} runs with expired worker sessions.", failed);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Checking for expired worker sessions failed.");
            }
        }
    }
    catch (OperationCanceledException) { }
}));

app.Run("http://127.0.0.1:5080");

internal sealed record CreateAccountRequest(string Kind);
internal sealed record FundingRequest(string IdempotencyKey, long AmountSen, long TransactionLimitSen);
internal sealed record TransferRequest(string IdempotencyKey, Guid SourceAccountId, Guid DestinationAccountId, long AmountSen);
internal sealed record SavingsRequest(string IdempotencyKey, Guid CashAccountId, Guid SavingsAccountId, long AmountSen);
internal sealed record BillPaymentRequest(string IdempotencyKey, Guid CashAccountId, Guid ObligationId);
internal sealed record CancelRunRequest(string? Reason);
internal sealed record FinishRunRequest(string? Outcome, string? Reason);
internal sealed record ReversalRequest(string IdempotencyKey, string Reason);
internal sealed record CreateObligationRequest(Guid MerchantAccountId, long AmountSen, string InvoiceReference);
