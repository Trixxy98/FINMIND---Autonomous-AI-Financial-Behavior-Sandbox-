using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Finmind")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Finmind before starting the API.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresLedger>();
builder.Services.AddSingleton<ResearchReads>();
builder.Services.AddSingleton<BatchStore>();
builder.Services.AddSingleton<ReviewStore>();
builder.Services.AddSingleton<ExportLog>();
builder.Services.AddSingleton(sp => new ToolGateway(sp.GetRequiredService<PostgresLedger>(), TimeProvider.System,
    AgentGatewayRules.IsValidSessionLifetime(builder.Configuration["FINMIND_SESSION_LIFETIME_SECONDS"])
        ? AgentGatewayRules.SessionLifetimeFrom(builder.Configuration["FINMIND_SESSION_LIFETIME_SECONDS"]) : null));
var app = builder.Build();

// ADR-0013 A3: refuse to start with unsafe settings or a privileged database role.
var unsafeSettings = StartupChecks.Problems(name => builder.Configuration[name]).ToList();
if (StartupChecks.PrivilegedRole(app.Services.GetRequiredService<NpgsqlDataSource>()) is { } roleProblem)
    unsafeSettings.Add(roleProblem);
if (unsafeSettings.Count > 0)
    throw new InvalidOperationException("Refusing to start: " + string.Join(" ", unsafeSettings));

var guard = RequestGuardOptions.Create(builder.Configuration["FINMIND_RESEARCHER_TOKEN"], builder.Configuration["FINMIND_ALLOWED_ORIGINS"]);
if (!guard.ResearcherConfigured)
    app.Logger.LogWarning("FINMIND_RESEARCHER_TOKEN is missing or shorter than 32 characters; researcher routes answer 401.");
app.Use(async (context, next) =>
{
    var authorization = context.Request.Headers.Authorization.ToString();
    var bearer = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization["Bearer ".Length..].Trim() : null;
    if (RequestGuard.Check(context.Request.Path.Value ?? "", context.Request.Host.Value ?? "",
            context.Request.Headers.Origin.ToString(), bearer, guard) is { } refused)
    {
        if (refused == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = refused;
        await context.Response.WriteAsJsonAsync(new { error = refused == 401 ? "Researcher credential required." : "Request not allowed from this host or origin." });
        return;
    }
    await next();
});

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

// ADR-0013 part B: researcher read API. The request guard has already checked the researcher credential.
var research = app.MapGroup("/api/v1");
IResult NotFound() => Results.Json(LedgerHttpResponses.ForError(LedgerReasonCode.RunNotFound, "Run does not exist.").Body, statusCode: 404);
IResult Invalid(string field) => Results.BadRequest(new { error = $"Invalid {field}." });
int Limit(int? limit) => Math.Clamp(limit ?? ResearchReads.DefaultLimit, 1, ResearchReads.MaxLimit);

research.MapGet("/runs", (string? status, string? kind, int? limit, Guid? before, ResearchReads reads) =>
{
    if (status is not null && !ResearchReads.Statuses.Contains(status)) return Invalid("status");
    bool? managed = kind switch { null => null, "managed" => true, "legacy" => false, _ => (bool?)false };
    if (kind is not (null or "managed" or "legacy")) return Invalid("kind");
    return Execute(() => Results.Ok(reads.ListRuns(status, managed, Limit(limit), before)), isRead: true);
});
// ADR-0014: run control for the UI. Same ledger rules and events as the /internal routes.
research.MapPost("/runs", (JsonObject? config, PostgresLedger ledger) =>
    Execute(() =>
    {
        var run = ledger.CreateManagedRun(config, codeRevision);
        return Results.Created($"/api/v1/runs/{run.RunId}", run);
    }));
research.MapPost("/runs/{runId:guid}/start", (Guid runId, ResearchReads reads, PostgresLedger ledger) =>
    Execute(() => reads.RunExists(runId)
        ? Results.Ok(ledger.TransitionRun(runId, RunStatus.Queued, null, FinanceEvents.InternalApiSource)) : NotFound()));
research.MapPost("/runs/{runId:guid}/cancel", (Guid runId, CancelRunRequest? request, ResearchReads reads, PostgresLedger ledger) =>
    Execute(() => reads.RunExists(runId)
        ? Results.Ok(ledger.TransitionRun(runId, RunStatus.Cancelled, request?.Reason, FinanceEvents.InternalApiSource)) : NotFound()));
research.MapGet("/runs/{runId:guid}/verify", (Guid runId, ResearchReads reads, PostgresLedger ledger) =>
    Execute(() =>
    {
        if (!reads.RunExists(runId)) return NotFound();
        var broken = ledger.VerifyEventChain(runId);
        return Results.Ok(new { runId, intact = broken is null, brokenAtSequence = broken });
    }, isRead: true));

research.MapGet("/runs/{runId:guid}", (Guid runId, ResearchReads reads) =>
    Execute(() => reads.GetRun(runId) is { } run ? Results.Ok(run) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/metrics", (Guid runId, ResearchReads reads) =>
    Execute(() => reads.Metrics(runId) is { } metrics ? Results.Ok(metrics) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/outcomes", (Guid runId, ResearchReads reads) =>
    Execute(() => reads.Outcomes(runId) is { } outcomes ? Results.Ok(outcomes) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/wallets", (Guid runId, ResearchReads reads) =>
    Execute(() => reads.Wallets(runId) is { } wallets ? Results.Ok(wallets) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/ledger", (Guid runId, long? afterSequence, int? limit, ResearchReads reads) =>
    Execute(() => reads.Ledger(runId, Math.Max(afterSequence ?? 0, 0), Limit(limit)) is { } ledger ? Results.Ok(ledger) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/actions", (Guid runId, long? afterSequence, int? limit, ResearchReads reads) =>
    Execute(() => reads.ListActions(runId, Math.Max(afterSequence ?? 0, 0), Limit(limit)) is { } actions ? Results.Ok(actions) : NotFound(), isRead: true));
research.MapGet("/runs/{runId:guid}/events", (Guid runId, long? afterSequence, int? limit, ResearchReads reads, PostgresLedger ledger) =>
    Execute(() =>
    {
        if (!reads.RunExists(runId)) return NotFound();
        var after = Math.Max(afterSequence ?? 0, 0);
        var events = ledger.GetEvents(runId, after, Limit(limit));
        return Results.Ok(new { runId, events, nextAfterSequence = events.Count > 0 ? events[^1].Sequence : after });
    }, isRead: true));
// ADR-0013 B2: server-sent events. Resume with Last-Event-ID or afterSequence.
research.MapGet("/runs/{runId:guid}/stream", async (Guid runId, string? afterSequence, HttpContext context,
    ResearchReads reads, PostgresLedger ledger) =>
{
    if (EventStream.Cursor(context.Request.Headers["Last-Event-ID"], afterSequence) is not { } cursor) return Invalid("cursor");
    if (!reads.RunExists(runId)) return NotFound();
    await EventStream.Run(context, runId, cursor, ledger, reads);
    return Results.Empty;
});

// ADR-0015 step 2: batches of replications, and their comparison.
research.MapPost("/batches", (JsonObject? body, PostgresLedger ledger, BatchStore batches) => Execute(() =>
{
    var fields = new List<string>();
    if (body is null) return Results.BadRequest(new { error = "Body must be JSON.", fields = new[] { "body" } });
    fields.AddRange(body.Select(p => p.Key).Where(k => k is not ("config" or "count" or "seeds" or "label" or "start")));
    var config = body["config"] as JsonObject;
    if (config is null) fields.Add("config");
    int count = ToolDefinition.TryGetInteger(body["count"], out var n) && n is >= 1 and <= 20 ? (int)n : 0;
    if (count == 0) fields.Add("count");
    long[]? seeds = null;
    if (body.ContainsKey("seeds"))
    {
        seeds = body["seeds"] is JsonArray list && list.All(s => ToolDefinition.TryGetInteger(s, out var v) && v is >= 0 and <= int.MaxValue)
            ? list.Select(s => { ToolDefinition.TryGetInteger(s, out var v); return v; }).ToArray() : null;
        if (seeds is null || seeds.Length != count) fields.Add("seeds");
    }
    var label = body["label"] is JsonValue text && text.TryGetValue<string>(out var given) ? given.Trim() : null;
    if (body.ContainsKey("label") && (label is null || label.Length is 0 or > 200)) fields.Add("label");
    var start = body["start"] is JsonValue flag && flag.TryGetValue<bool>(out var s) && s;
    if (fields.Count > 0) return Results.BadRequest(new { error = "Invalid batch request.", fields = fields.Distinct() });

    // Every configuration is validated before anything is stored, so a bad request creates nothing.
    JsonObject Variant(int index)
    {
        var variant = (JsonObject)config!.DeepClone();
        if (seeds is not null) variant["seed"] = seeds[index];
        return variant;
    }
    for (var i = 0; i < count; i++) RunConfiguration.Validate(Variant(i), codeRevision);

    var orderSeed = (long)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);
    var order = Comparison.StartOrder(count, orderSeed);
    var batchId = batches.Insert(label, config!, count, seeds is null ? "same" : "list", orderSeed, order, seeds);
    var created = Enumerable.Range(0, count).Select(i => ledger.CreateManagedRun(Variant(i), codeRevision, batchId, i).RunId).ToArray();
    var inStartOrder = order.Select(position => created[position]).ToArray();
    if (start) foreach (var runId in inStartOrder) ledger.TransitionRun(runId, RunStatus.Queued, null, FinanceEvents.InternalApiSource);
    return Results.Created($"/api/v1/batches/{batchId}", new { batchId, runIds = inStartOrder, orderSeed, startOrder = order, started = start });
}));
research.MapGet("/batches", (int? limit, BatchStore batches) =>
    Execute(() => Results.Ok(batches.List(Limit(limit))), isRead: true));
research.MapGet("/batches/{batchId:guid}", (Guid batchId, BatchStore batches) =>
    Execute(() => batches.Get(batchId) is { } batch ? Results.Ok(batch)
        : Results.Json(new { status = "denied", reasonCode = "BATCH_NOT_FOUND", message = "Batch does not exist." }, statusCode: 404), isRead: true));

research.MapGet("/compare", (HttpRequest request, BatchStore batches, ResearchReads reads) => Execute(() =>
{
    var ids = request.Query["batch"].Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).ToList();
    if (ids.Count is < 2 or > 6 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
        return Results.BadRequest(new { error = "Give 2 to 6 distinct batch IDs as batch=..." });
    var groups = new List<(JsonObject Batch, List<JsonObject> Snapshots, List<JsonObject> Included, JsonArray Excluded)>();
    foreach (var id in ids)
    {
        if (batches.Get(id) is not { } batch)
            return Results.Json(new { status = "denied", reasonCode = "BATCH_NOT_FOUND", message = $"Batch {id} does not exist." }, statusCode: 404);
        var snapshots = new List<JsonObject>();
        var included = new List<JsonObject>();
        var excluded = new JsonArray();
        foreach (var entry in batch["runs"]!.AsArray())
        {
            var runId = entry!["runId"]!.GetValue<Guid>();
            var run = reads.GetRun(runId)!;
            snapshots.Add(run["configSnapshot"]!.AsObject());
            if (Comparison.ExclusionReason((bool)run["researchEligible"]!, (string?)run["status"], (string?)run["statusReason"]) is { } reason)
                excluded.Add(new JsonObject { ["runId"] = runId, ["reason"] = reason, ["statusReason"] = run["statusReason"]?.DeepClone() });
            else included.Add(reads.Metrics(runId)!);
        }
        groups.Add((batch, snapshots, included, excluded));
    }

    var metrics = new JsonArray();
    foreach (var (name, path, unit) in Comparison.Metrics)
    {
        var perGroup = new JsonArray();
        foreach (var group in groups)
        {
            var values = group.Included.Select(m => (RunId: m["runId"]!.GetValue<Guid>(), Value: Comparison.Read(m, path))).ToList();
            var summary = unit is { } u
                ? Comparison.Summarize(values.Select(v => ToolDefinition.TryGetInteger(v.Value, out var x) ? x
                    : throw new InvalidOperationException($"{name} is not an integer.")).ToList(), u)
                : Comparison.SummarizeRates(values.Select(v => v.Value is JsonValue x ? x.GetValue<double>() : (double?)null).ToList());
            summary["batchId"] = group.Batch["batchId"]!.DeepClone();
            summary["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["runId"] = v.RunId, ["value"] = v.Value?.DeepClone() }).ToArray());
            perGroup.Add(summary);
        }
        metrics.Add(new JsonObject { ["name"] = name, ["groups"] = perGroup });
    }
    return Results.Ok(new JsonObject
    {
        ["comparisonVersion"] = Comparison.Version,
        ["metricsVersion"] = MetricsV1.Version,
        ["note"] = "Descriptive only. No p-values; an interval only from n >= 5. One configuration and seed per group unless a seed list was used.",
        ["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject
        {
            ["batchId"] = g.Batch["batchId"]!.DeepClone(), ["label"] = g.Batch["label"]?.DeepClone(),
            ["n"] = g.Included.Count, ["included"] = new JsonArray(g.Included.Select(m => m["runId"]!.DeepClone()).ToArray()),
            ["excluded"] = g.Excluded
        }).ToArray()),
        ["configuration"] = Comparison.Differences(groups.Select(g => (IReadOnlyList<JsonObject>)g.Snapshots).ToList()),
        ["metrics"] = metrics,
        // ADR-0018: outcomes of the included runs; each group lists runs that never reached a situation apart.
        ["outcomesVersion"] = OutcomesV1.Version,
        ["outcomes"] = Comparison.SummarizeOutcomes(groups.Select(g => (g.Batch["batchId"]!,
            (IReadOnlyList<JsonObject>)g.Included.Select(m => reads.Outcomes(m["runId"]!.GetValue<Guid>())!).ToList())).ToList())
    });
}, isRead: true));

// ADR-0015 step 3: rule labels, review candidates, and append-only human reviews.
research.MapGet("/runs/{runId:guid}/classifications", (Guid runId, ResearchReads reads, ReviewStore reviews) =>
    Execute(() => reads.Classifications(runId) is { } classified
        ? Results.Ok(Classifier.WithReviews(classified, reviews.For(runId), (int)classified["actions"]!))
        : NotFound(), isRead: true));
research.MapPost("/runs/{runId:guid}/reviews", (Guid runId, JsonObject? body, ResearchReads reads, ReviewStore reviews) => Execute(() =>
{
    if (reads.Classifications(runId) is not { } classified) return NotFound();
    var key = body?["key"] is JsonValue k && k.TryGetValue<string>(out var keyText) ? keyText : null;
    var label = body?["label"] is JsonValue l && l.TryGetValue<string>(out var labelText) ? labelText : null;
    var note = body?["note"] is JsonValue n && n.TryGetValue<string>(out var noteText) ? noteText.Trim() : null;
    string? invalid = body is null || body.Any(p => p.Key is not ("key" or "label" or "note")) ? "body"
        : key is null || classified["items"]!.AsArray().All(i => (string)i!["key"]! != key) ? "key"
        : Classifier.InvalidReviewField(label, note);
    if (invalid is not null) return Results.BadRequest(new { error = "Invalid review.", fields = new[] { invalid } });
    var review = reviews.Add(runId, key!, label!, string.IsNullOrEmpty(note) ? null : note);
    return Results.Created($"/api/v1/runs/{runId}/classifications", review);
}));

// ADR-0015 step 4: read-only exports from one snapshot.
research.MapGet("/runs/{runId:guid}/export", (Guid runId, string? format, HttpContext context, ResearchReads reads, ExportLog exports) => Execute(() =>
{
    // ADR-0017: each request is recorded first, whatever its outcome.
    if (format is not ("jsonl" or "csv"))
    {
        exports.Record(runId, format, "invalid_format");
        return Results.BadRequest(new { error = "format must be jsonl or csv." });
    }
    if (reads.Export(runId) is not { } records)
    {
        exports.Record(runId, format, "not_found");
        return NotFound();
    }
    exports.Record(runId, format, "served");
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"finmind-run-{runId}.{format}\"";
    return format == "jsonl"
        ? Results.Text(RunExport.Jsonl(records), "application/x-ndjson; charset=utf-8")
        : Results.Text(RunExport.PostingsCsv(records), "text/csv; charset=utf-8");
}, isRead: true));

// ADR-0017 step 2: the operator switch and the export record.
object SwitchView(OperatorState state, IReadOnlyList<OperatorSwitchEntry> history) => new
{
    stopped = state.Stopped, reason = state.Reason, since = state.Since,
    history = history.Select(h => new { action = h.Action, reason = h.Reason, cancelledRuns = h.CancelledRuns, at = h.At })
};
research.MapGet("/admin/status", (PostgresLedger ledger) =>
    Execute(() => Results.Ok(SwitchView(ledger.GetOperatorState(), ledger.OperatorHistory(20))), isRead: true));
research.MapPost("/admin/stop", (AdminReasonRequest? request, PostgresLedger ledger) => Execute(() =>
{
    var result = ledger.StopAll(request?.Reason, FinanceEvents.InternalApiSource);
    return Results.Ok(new { stopped = true, reason = result.State.Reason, since = result.State.Since, cancelledRunIds = result.CancelledRunIds });
}));
research.MapPost("/admin/resume", (AdminReasonRequest? request, PostgresLedger ledger) =>
    Execute(() => Results.Ok(SwitchView(ledger.ResumeAll(request?.Reason, FinanceEvents.InternalApiSource), ledger.OperatorHistory(20)))));
research.MapGet("/admin/exports", (int? limit, ExportLog exports) => Execute(() => Results.Ok(new
{
    exports = exports.Latest(limit ?? 100).Select(e => new { runId = e.RunId, format = e.Format, outcome = e.Outcome, at = e.At })
}), isRead: true));

// ADR-0015 step 5b: finished research runs that still have review candidates, newest first.
research.MapGet("/reviews/pending", (int? limit, ResearchReads reads, ReviewStore reviews) => Execute(() =>
{
    var scan = Math.Clamp(limit ?? 50, 1, 100);
    var runs = reads.ListRuns(null, true, scan, null)["runs"]!.AsArray()
        .Where(r => r!["status"] is JsonValue s && AgentGatewayRules.IsTerminal(RunLifecycle.Parse(s.GetValue<string>()))).ToList();
    var pending = new JsonArray();
    foreach (var run in runs)
    {
        var runId = run!["runId"]!.GetValue<Guid>();
        var classified = reads.Classifications(runId)!;
        var counts = Classifier.WithReviews(classified, reviews.For(runId), (int)classified["actions"]!)["counts"]!;
        if ((int)counts["unresolved"]! == 0) continue;
        pending.Add(new JsonObject
        {
            ["runId"] = runId, ["scenarioId"] = run["scenarioId"]?.DeepClone(), ["agent"] = run["agent"]?.DeepClone(),
            ["status"] = run["status"]?.DeepClone(), ["finishedAt"] = run["finishedAt"]?.DeepClone(),
            ["unresolved"] = counts["unresolved"]!.DeepClone(), ["candidates"] = counts["candidates"]!.DeepClone()
        });
    }
    return Results.Ok(new JsonObject
    {
        ["classificationVersion"] = Classifier.Version, ["scanned"] = runs.Count, ["runs"] = pending,
        ["note"] = $"The newest {scan} research runs were checked; finished runs only."
    });
}, isRead: true));

research.MapGet("/scenarios", () => Results.Ok(new
{
    scenarios = ScenarioCatalog.All.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ThenBy(s => s.Version).Select(s => new
    {
        s.Id, s.Version, versionLabel = s.VersionLabel, s.Title, s.RecommendedObjective, fileHash = s.FileHash,
        latest = ScenarioCatalog.Latest(s.Id)!.Version == s.Version
    })
}));
research.MapGet("/models", () => Results.Ok(new
{
    pricingVersion = PricingCatalog.Version, // kept for older clients; each model now names its own provider's version
    models = RunCatalogs.Models.OrderBy(p => p.Key, StringComparer.Ordinal)
        .SelectMany(p => p.Value.Order(StringComparer.Ordinal).Select(model => new
        {
            provider = p.Key, modelId = model, paid = ProviderCatalog.IsPaid(p.Key),
            providerLabel = ProviderCatalog.Paid.TryGetValue(p.Key, out var info) ? info.Label : "Mock (no provider)",
            pricingVersion = PricingCatalog.VersionFor(p.Key),
            // ADR-0019: false until a smoke run against the provider's live API has been reported.
            verifiedLive = info?.VerifiedLive ?? true,
            efforts = info is null ? [] : new[] { "low", "medium", "high", "xhigh", "max" }.Where(info.Efforts.Contains).ToArray(),
            notice = info?.Notice
        }))
}));

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
        // ADR-0013 item 18: an authenticated tool call is always recorded, readable or not.
        var (call, unreadable) = await ToolCallBody.Read(request.Body);
        return Execute(() => Results.Json(call is not null
            ? gateway.Execute(runId, call.ToolCallId, call.Tool, call.Arguments, call.HadInvalidUnicode)
            : gateway.RecordUnreadable(runId, unreadable!)));
    });

    worker.MapPost("/session/model-reservations", (HttpRequest request, JsonObject? body, ToolGateway gateway) =>
        WithSession(request, gateway, runId => Execute(() => Results.Json(gateway.ReserveModelCall(runId, body)))));

    // The only route that accepts a revoked or expired session: it can only report usage (ADR-0013 A1).
    // Read raw, so that invalid Unicode in the model's text is replaced and does not lose the usage (ADR-0013 item 17).
    worker.MapPost("/session/model-invocations", async (HttpRequest request, ToolGateway gateway) =>
    {
        if (gateway.ResolveSessionForUsage(Bearer(request)) is not { } runId) return Results.StatusCode(401);
        using var raw = new MemoryStream();
        var chunk = new byte[16_384];
        int count;
        while ((count = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (raw.Length + count > 1_048_576) return Results.StatusCode(413);
            raw.Write(chunk, 0, count);
        }

        JsonNode? body;
        try { body = AgentText.Parse(raw.ToArray()).Body; }
        catch (JsonException) { return Results.BadRequest(new { error = "Body must be JSON." }); }
        return Execute(() => Results.Json(gateway.RecordModelInvocation(runId, body)));
    });

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
internal sealed record AdminReasonRequest(string? Reason);
internal sealed record FinishRunRequest(string? Outcome, string? Reason);
internal sealed record ReversalRequest(string IdempotencyKey, string Reason);
internal sealed record CreateObligationRequest(Guid MerchantAccountId, long AmountSen, string InvoiceReference);
