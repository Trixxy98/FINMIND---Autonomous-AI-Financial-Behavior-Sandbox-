using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Finmind")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Finmind before starting the API.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresLedger>();
var app = builder.Build();

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
    Execute(() => CommitResponse(ledger.Transfer(runId, request.IdempotencyKey, request.SourceAccountId, request.DestinationAccountId, request.AmountSen))));

app.MapPost("/internal/runs/{runId:guid}/savings", (Guid runId, SavingsRequest request, PostgresLedger ledger) =>
    Execute(() => CommitResponse(ledger.MoveToSavings(runId, request.IdempotencyKey, request.CashAccountId, request.SavingsAccountId, request.AmountSen))));

app.MapPost("/internal/runs/{runId:guid}/bill-payments", (Guid runId, BillPaymentRequest request, PostgresLedger ledger) =>
    Execute(() => CommitResponse(ledger.PayObligation(runId, request.IdempotencyKey, request.CashAccountId, request.ObligationId))));

// ADR-0006: researcher-initiated full reversal. The initiator is set here, never read from the body.
// Unauthenticated localhost-only scaffolding; the agent tool gateway must never expose this route.
app.MapPost("/internal/runs/{runId:guid}/journals/{journalId:guid}/reversals", (Guid runId, Guid journalId, ReversalRequest request, PostgresLedger ledger) =>
    Execute(() => CommitResponse(ledger.ReverseJournal(runId, request.IdempotencyKey, journalId, request.Reason, ReversalInitiator.Researcher))));

// Obligation setup never moves money; success keeps returning the stored obligation.
app.MapPost("/internal/runs/{runId:guid}/obligations", (Guid runId, CreateObligationRequest request, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.CreateObligation(runId, request.MerchantAccountId, request.AmountSen, request.InvoiceReference))));

app.MapGet("/internal/runs/{runId:guid}/obligations/{obligationId:guid}", (Guid runId, Guid obligationId, PostgresLedger ledger) =>
    Execute(() => Results.Ok(ledger.GetObligation(runId, obligationId)), isRead: true));

// ADR-0007: ordered, append-only finance events for one run, paged by sequence cursor.
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

app.Run("http://127.0.0.1:5080");

internal sealed record CreateAccountRequest(string Kind);
internal sealed record FundingRequest(string IdempotencyKey, long AmountSen, long TransactionLimitSen);
internal sealed record TransferRequest(string IdempotencyKey, Guid SourceAccountId, Guid DestinationAccountId, long AmountSen);
internal sealed record SavingsRequest(string IdempotencyKey, Guid CashAccountId, Guid SavingsAccountId, long AmountSen);
internal sealed record BillPaymentRequest(string IdempotencyKey, Guid CashAccountId, Guid ObligationId);
internal sealed record CancelRunRequest(string? Reason);
internal sealed record ReversalRequest(string IdempotencyKey, string Reason);
internal sealed record CreateObligationRequest(Guid MerchantAccountId, long AmountSen, string InvoiceReference);
