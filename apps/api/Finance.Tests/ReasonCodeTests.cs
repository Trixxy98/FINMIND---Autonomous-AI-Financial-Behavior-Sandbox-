using System.Text.RegularExpressions;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0005: every rejection has a stable reason code, both ledgers agree, and rejections never change state.
public sealed class ReasonCodeTests
{
    private sealed record Fixture(
        Guid RunId, Guid Cash, Guid Savings, Guid Merchant, Guid OtherRunCash,
        ObligationResult Open, ObligationResult Paid, ObligationResult OverCap);

    private static Fixture Setup(IFinanceLedger ledger)
    {
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 100).CashAccountId;
        var savings = ledger.CreateAccount(runId, AccountKind.AgentSavings);
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
        var otherRunCash = ledger.OpenRun(Guid.NewGuid(), "open", 1_000, 100).CashAccountId;
        var open = ledger.CreateObligation(runId, merchant, 50, "INV-OPEN");
        var paid = ledger.CreateObligation(runId, merchant, 40, "INV-PAID");
        var overCap = ledger.CreateObligation(runId, merchant, 150, "INV-OVER-CAP");
        ledger.PayObligation(runId, "pay-paid", cash, paid.ObligationId);
        ledger.Transfer(runId, "existing-transfer", cash, merchant, 10);
        return new Fixture(runId, cash, savings, merchant, otherRunCash, open, paid, overCap);
    }

    private static readonly Dictionary<string, (Action<IFinanceLedger, Fixture> Act, LedgerReasonCode Expected)> Scenarios = new()
    {
        ["transfer_zero_amount"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, f.Merchant, 0), LedgerReasonCode.InvalidAmount),
        ["transfer_negative_amount"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, f.Merchant, -5), LedgerReasonCode.InvalidAmount),
        ["transfer_missing_key"] = ((l, f) => l.Transfer(f.RunId, " ", f.Cash, f.Merchant, 10), LedgerReasonCode.MissingRequiredField),
        ["transfer_missing_destination"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, Guid.Empty, 10), LedgerReasonCode.MissingRequiredField),
        ["transfer_same_account"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, f.Cash, 10), LedgerReasonCode.SameSourceAndDestination),
        ["transfer_cross_run_destination"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, f.OtherRunCash, 10), LedgerReasonCode.AccountNotInRun),
        ["transfer_from_merchant"] = ((l, f) => l.Transfer(f.RunId, "k", f.Merchant, f.Cash, 10), LedgerReasonCode.AccountKindNotPermitted),
        ["transfer_over_limit"] = ((l, f) => l.Transfer(f.RunId, "k", f.Cash, f.Merchant, 101), LedgerReasonCode.ExceedsTransactionLimit),
        ["transfer_insufficient_funds"] = ((l, f) => l.Transfer(f.RunId, "k", f.Savings, f.Merchant, 10), LedgerReasonCode.InsufficientFunds),
        ["transfer_reused_key"] = ((l, f) => l.Transfer(f.RunId, "existing-transfer", f.Cash, f.Merchant, 11), LedgerReasonCode.IdempotencyKeyConflict),
        ["savings_to_merchant"] = ((l, f) => l.MoveToSavings(f.RunId, "k", f.Cash, f.Merchant, 10), LedgerReasonCode.AccountKindNotPermitted),
        ["pay_unknown_obligation"] = ((l, f) => l.PayObligation(f.RunId, "k", f.Cash, Guid.NewGuid()), LedgerReasonCode.ObligationNotFound),
        ["pay_already_paid"] = ((l, f) => l.PayObligation(f.RunId, "pay-again", f.Cash, f.Paid.ObligationId), LedgerReasonCode.ObligationAlreadyPaid),
        ["pay_over_limit"] = ((l, f) => l.PayObligation(f.RunId, "k", f.Cash, f.OverCap.ObligationId), LedgerReasonCode.ExceedsTransactionLimit),
        ["pay_from_savings"] = ((l, f) => l.PayObligation(f.RunId, "k", f.Savings, f.Open.ObligationId), LedgerReasonCode.AccountKindNotPermitted),
        ["pay_from_other_run"] = ((l, f) => l.PayObligation(f.RunId, "k", f.OtherRunCash, f.Open.ObligationId), LedgerReasonCode.AccountNotInRun),
        ["pay_reused_key"] = ((l, f) => l.PayObligation(f.RunId, "existing-transfer", f.Cash, f.Open.ObligationId), LedgerReasonCode.IdempotencyKeyConflict),
        ["obligation_padded_reference"] = ((l, f) => l.CreateObligation(f.RunId, f.Merchant, 10, " INV-1"), LedgerReasonCode.InvalidInvoiceReference),
        ["obligation_reserved_reference"] = ((l, f) => l.CreateObligation(f.RunId, f.Merchant, 10, "legacy:x"), LedgerReasonCode.InvalidInvoiceReference),
        ["obligation_zero_amount"] = ((l, f) => l.CreateObligation(f.RunId, f.Merchant, 0, "INV-ZERO"), LedgerReasonCode.InvalidAmount),
        ["obligation_missing_payee"] = ((l, f) => l.CreateObligation(f.RunId, Guid.Empty, 10, "INV-NO-PAYEE"), LedgerReasonCode.MissingRequiredField),
        ["obligation_reference_conflict"] = ((l, f) => l.CreateObligation(f.RunId, f.Merchant, 51, "INV-OPEN"), LedgerReasonCode.InvoiceReferenceConflict),
        ["obligation_payee_not_merchant"] = ((l, f) => l.CreateObligation(f.RunId, f.Cash, 10, "INV-CASH"), LedgerReasonCode.AccountKindNotPermitted),
        ["obligation_payee_other_run"] = ((l, f) => l.CreateObligation(f.RunId, f.OtherRunCash, 10, "INV-OTHER"), LedgerReasonCode.AccountNotInRun),
        ["open_run_different_limit"] = ((l, f) => l.OpenRun(f.RunId, "open", 1_000, 200), LedgerReasonCode.RunPolicyConflict),
        ["open_run_different_amount"] = ((l, f) => l.OpenRun(f.RunId, "open", 2_000, 100), LedgerReasonCode.RunPolicyConflict),
        ["open_run_different_key"] = ((l, f) => l.OpenRun(f.RunId, "open-again", 1_000, 100), LedgerReasonCode.RunPolicyConflict),
        ["open_run_zero_amount"] = ((l, f) => l.OpenRun(Guid.NewGuid(), "open", 0, 100), LedgerReasonCode.InvalidAmount),
        ["open_run_zero_limit"] = ((l, f) => l.OpenRun(Guid.NewGuid(), "open", 100, 0), LedgerReasonCode.InvalidAmount),
        ["open_run_missing_key"] = ((l, f) => l.OpenRun(Guid.NewGuid(), " ", 100, 100), LedgerReasonCode.MissingRequiredField),
        ["create_account_missing_run"] = ((l, f) => l.CreateAccount(Guid.Empty, AccountKind.Merchant), LedgerReasonCode.MissingRequiredField),
        ["create_account_unknown_kind"] = ((l, f) => l.CreateAccount(f.RunId, (AccountKind)99), LedgerReasonCode.UnsupportedAccountKind),
        ["balance_other_run_account"] = ((l, f) => l.GetBalance(f.RunId, f.OtherRunCash), LedgerReasonCode.AccountNotInRun),
        ["get_unknown_obligation"] = ((l, f) => l.GetObligation(f.RunId, Guid.NewGuid()), LedgerReasonCode.ObligationNotFound),
    };

    public static TheoryData<string> ScenarioNames() => new(Scenarios.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void InMemory_rejection_has_expected_code_and_no_effect(string scenario) =>
        AssertRejection(new InMemoryLedger(), scenario);

    [PostgresTheory]
    [MemberData(nameof(ScenarioNames))]
    public void Postgres_rejection_has_same_code_and_no_effect(string scenario)
    {
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);
        AssertRejection(new PostgresLedger(dataSource), scenario);
    }

    private static void AssertRejection(IFinanceLedger ledger, string scenario)
    {
        var fixture = Setup(ledger);
        var before = Snapshot(ledger, fixture);
        var (act, expected) = Scenarios[scenario];

        var exception = Assert.ThrowsAny<LedgerException>(() => act(ledger, fixture));

        Assert.Equal(expected, exception.ReasonCode);
        Assert.Equal(before, Snapshot(ledger, fixture));
    }

    private static string Snapshot(IFinanceLedger ledger, Fixture f) => string.Join("|",
        ledger.GetBalance(f.RunId, f.Cash),
        ledger.GetBalance(f.RunId, f.Savings),
        ledger.GetBalance(f.RunId, f.Merchant),
        ledger.GetJournals(f.RunId).Count,
        ledger.GetObligation(f.RunId, f.Open.ObligationId).Paid,
        ledger.GetObligation(f.RunId, f.Paid.ObligationId).Paid,
        ledger.GetObligation(f.RunId, f.OverCap.ObligationId).Paid);

    [Fact]
    public void Retried_payment_is_a_replay_not_a_second_effect()
    {
        var ledger = new InMemoryLedger();
        var fixture = Setup(ledger);
        var before = Snapshot(ledger, fixture);

        var replay = ledger.PayObligation(fixture.RunId, "pay-paid", fixture.Cash, fixture.Paid.ObligationId);

        Assert.True(replay.WasIdempotentReplay);
        Assert.Equal("replayed", LedgerHttpResponses.StatusFor(replay));
        Assert.Equal(before, Snapshot(ledger, fixture));
    }

    [Fact]
    public void Every_reason_code_has_a_unique_explicit_wire_value_and_outcome()
    {
        var codes = Enum.GetValues<LedgerReasonCode>();
        var wireValues = codes.Select(LedgerReasonCodes.ToWireValue).ToArray();

        Assert.Equal(codes.Length, wireValues.Distinct().Count());
        Assert.All(wireValues, value => Assert.Matches(new Regex("^[A-Z]+(_[A-Z]+)*$"), value));
        Assert.All(codes, code => Assert.NotEqual(LedgerOutcome.Committed, LedgerReasonCodes.OutcomeOf(code)));
        Assert.All(codes, code => Assert.NotEqual(LedgerOutcome.Replayed, LedgerReasonCodes.OutcomeOf(code)));
    }

    [Theory]
    [InlineData(LedgerReasonCode.InvalidAmount, 400, "malformed")]
    [InlineData(LedgerReasonCode.ExceedsTransactionLimit, 422, "denied")]
    [InlineData(LedgerReasonCode.InsufficientFunds, 422, "denied")]
    [InlineData(LedgerReasonCode.IdempotencyKeyConflict, 409, "conflict")]
    [InlineData(LedgerReasonCode.ConcurrentStateChange, 500, "internal_error")]
    public void Error_responses_use_documented_status_and_body(LedgerReasonCode code, int statusCode, string status)
    {
        var (actualStatusCode, body) = LedgerHttpResponses.ForError(code, "diagnostic");

        Assert.Equal(statusCode, actualStatusCode);
        Assert.Equal(status, body.Status);
        Assert.Equal(LedgerReasonCodes.ToWireValue(code), body.ReasonCode);
        Assert.False(body.Committed);
        Assert.Null(body.JournalId);
    }

    [Fact]
    public void Internal_errors_do_not_expose_exception_details()
    {
        var (statusCode, body) = LedgerHttpResponses.ForException(
            new InvalidOperationException("Host=db;Password=secret-value"));

        Assert.Equal(500, statusCode);
        Assert.Equal("INTERNAL_ERROR", body.ReasonCode);
        Assert.Equal(LedgerHttpResponses.GenericInternalErrorMessage, body.Message);
        Assert.DoesNotContain("secret", body.Message);
    }

    [Fact]
    public void Overflow_maps_to_invalid_amount()
    {
        var (statusCode, body) = LedgerHttpResponses.ForException(new OverflowException());

        Assert.Equal(400, statusCode);
        Assert.Equal("INVALID_AMOUNT", body.ReasonCode);
    }

    [Fact]
    public void Read_endpoints_report_missing_resources_as_not_found()
    {
        var (statusCode, body) = LedgerHttpResponses.ForReadException(
            new LedgerValidationException(LedgerReasonCode.AccountNotInRun, "Account is not in this run."));

        Assert.Equal(404, statusCode);
        Assert.Equal("ACCOUNT_NOT_IN_RUN", body.ReasonCode);
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")))
            Skip = "Set FINMIND_TEST_DATABASE to run PostgreSQL integration tests.";
    }
}
