using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Finmind")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Finmind before starting the API.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Finmind.Api.Finance.PostgresLedger>();
var app = builder.Build();

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

app.MapPost("/internal/runs/{runId:guid}/accounts", (Guid runId, CreateAccountRequest request, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    if (runId == Guid.Empty || !Enum.TryParse<Finmind.Api.Finance.AccountKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind))
        return Results.BadRequest(new { error = "Unsupported account kind." });
    try
    {
        var accountId = ledger.CreateAccount(runId, kind);
        return Results.Created($"/internal/runs/{runId}/accounts/{accountId}", new { accountId, runId, kind = kind.ToString(), currency = "MYR" });
    }
    catch (Finmind.Api.Finance.LedgerValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/internal/runs/{runId:guid}/accounts/{accountId:guid}/balance", (Guid runId, Guid accountId, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try { return Results.Ok(new { runId, accountId, currency = "MYR", balanceSen = ledger.GetBalance(runId, accountId), source = "ledger" }); }
    catch (Finmind.Api.Finance.LedgerValidationException) { return Results.NotFound(); }
});

app.MapPost("/internal/runs/{runId:guid}/funding", (Guid runId, FundingRequest request, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    if (request.AmountSen <= 0) return Results.BadRequest(new { error = "Amount must be positive sen." });
    try
    {
        var result = ledger.OpenRun(runId, request.IdempotencyKey, request.AmountSen, request.TransactionLimitSen);
        return Results.Ok(new { result.JournalId, result.Committed, result.WasIdempotentReplay, cashAccountId = result.CashAccountId, balanceSen = ledger.GetBalance(runId, result.CashAccountId), currency = "MYR" });
    }
    catch (Finmind.Api.Finance.IdempotencyConflictException) { return Results.Conflict(new { error = "Idempotency key reused with a different payload." }); }
    catch (Finmind.Api.Finance.LedgerValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (OverflowException) { return Results.BadRequest(new { error = "Amount exceeds supported range." }); }
});

app.MapPost("/internal/runs/{runId:guid}/transfers", (Guid runId, TransferRequest request, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try
    {
        var result = ledger.Transfer(runId, request.IdempotencyKey, request.SourceAccountId, request.DestinationAccountId, request.AmountSen);
        return Results.Ok(new { result.JournalId, result.Committed, result.WasIdempotentReplay });
    }
    catch (Finmind.Api.Finance.IdempotencyConflictException) { return Results.Conflict(new { error = "Idempotency key reused with a different payload." }); }
    catch (Finmind.Api.Finance.LedgerValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (OverflowException) { return Results.BadRequest(new { error = "Amount exceeds supported range." }); }
});

app.MapPost("/internal/runs/{runId:guid}/savings", (Guid runId, SavingsRequest request, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try
    {
        var result = ledger.MoveToSavings(runId, request.IdempotencyKey, request.CashAccountId, request.SavingsAccountId, request.AmountSen);
        return Results.Ok(new { result.JournalId, result.Committed, result.WasIdempotentReplay });
    }
    catch (Finmind.Api.Finance.IdempotencyConflictException) { return Results.Conflict(new { error = "Idempotency key reused with a different payload." }); }
    catch (Finmind.Api.Finance.LedgerValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (OverflowException) { return Results.BadRequest(new { error = "Amount exceeds supported range." }); }
});

app.MapPost("/internal/runs/{runId:guid}/bill-payments", (Guid runId, BillPaymentRequest request, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try
    {
        var result = ledger.PayObligation(runId, request.IdempotencyKey, request.CashAccountId, request.ObligationId);
        return Results.Ok(new { result.JournalId, result.Committed, result.WasIdempotentReplay });
    }
    catch (Finmind.Api.Finance.IdempotencyConflictException) { return Results.Conflict(new { error = "Idempotency key reused with a different payload." }); }
    catch (Finmind.Api.Finance.LedgerValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (OverflowException) { return Results.BadRequest(new { error = "Amount exceeds supported range." }); }
});

app.MapPost("/internal/runs/{runId:guid}/obligations",
    (Guid runId,
     CreateObligationRequest request,
     Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try
    {
        var obligation = ledger.CreateObligation(
            runId,
            request.MerchantAccountId,
            request.AmountSen,
            request.InvoiceReference);

        return Results.Ok(obligation);
    }
    catch (Finmind.Api.Finance.IdempotencyConflictException)
    {
        return Results.Conflict(new
        {
            error = "Invoice reference reused with different details."
        });
    }
    catch (Finmind.Api.Finance.LedgerValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/internal/runs/{runId:guid}/obligations/{obligationId:guid}", (Guid runId, Guid obligationId, Finmind.Api.Finance.PostgresLedger ledger) =>
{
    try { return Results.Ok(ledger.GetObligation(runId, obligationId)); }
    catch (Finmind.Api.Finance.LedgerValidationException) { return Results.NotFound(); }
});

app.Run("http://127.0.0.1:5080");

internal sealed record CreateAccountRequest(string Kind);
internal sealed record FundingRequest(string IdempotencyKey, long AmountSen, long TransactionLimitSen);
internal sealed record TransferRequest(string IdempotencyKey, Guid SourceAccountId, Guid DestinationAccountId, long AmountSen);
internal sealed record SavingsRequest(string IdempotencyKey, Guid CashAccountId, Guid SavingsAccountId, long AmountSen);
internal sealed record BillPaymentRequest(string IdempotencyKey, Guid CashAccountId, Guid ObligationId);
internal sealed record CreateObligationRequest(Guid MerchantAccountId, long AmountSen, string InvoiceReference);
