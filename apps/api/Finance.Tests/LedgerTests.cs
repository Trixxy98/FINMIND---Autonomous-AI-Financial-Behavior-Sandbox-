using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

public sealed class LedgerTests
{
    [Fact]
    public void Commit_balanced_entry_moves_virtual_funds()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();

        var result = ledger.OpenRun(run, "opening-1", 100_000, 200);
        var journal = Assert.Single(ledger.GetJournals(run));
        var treasury = journal.Postings.Single(posting => posting.AmountSen < 0).AccountId;
        Assert.True(result.Committed);
        Assert.Equal(result.JournalId, journal.JournalId);
        Assert.Equal(0L, journal.Postings.Sum(posting => posting.AmountSen));
        Assert.Equal(100_000, ledger.GetBalance(run, result.CashAccountId));
        Assert.Equal(-100_000, ledger.GetBalance(run, treasury));
    }

    [Fact]
    public void Reject_unbalanced_entry_without_changing_balances()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        Assert.Throws<LedgerValidationException>(() => ledger.Commit(new JournalRequest(run, "bad-1", JournalKind.Transfer, "transfer", [new PostingRequest(cash, -10), new PostingRequest(merchant, 9)])));
        Assert.Equal(0, ledger.GetBalance(run, cash));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
    }

    [Fact]
    public void Commit_rejects_treasury_funding_disguised_as_transfer()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var request = new JournalRequest(run, "unauthorized-mint", JournalKind.Transfer, "virtual transfer",
            [new PostingRequest(treasury, -1_000), new PostingRequest(cash, 1_000)]);

        Assert.Throws<LedgerValidationException>(() => ledger.Commit(request));
        Assert.Equal(0, ledger.GetBalance(run, treasury));
        Assert.Equal(0, ledger.GetBalance(run, cash));
        Assert.Empty(ledger.GetJournals(run));
    }

    [Fact]
    public void Commit_rejects_second_opening_funding_journal_for_run()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var opening = ledger.OpenRun(run, "opening-first", 100, 50);

        var journal = Assert.Single(ledger.GetJournals(run));
        var treasury = journal.Postings.Single(posting => posting.AmountSen < 0).AccountId;
        var second = new JournalRequest(run, "opening-second", JournalKind.OpeningFunding, "opening", [new(treasury, -200), new(opening.CashAccountId, 200)]);

        Assert.Throws<LedgerValidationException>(() => ledger.Commit(second));
        Assert.Equal(100, ledger.GetBalance(run, opening.CashAccountId));
        Assert.Equal(-100, ledger.GetBalance(run, treasury));
        Assert.Single(ledger.GetJournals(run));
    }

    [Fact]
    public async Task Concurrent_run_initialization_creates_one_opening_journal_and_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => ledger.OpenRun(run, "concurrent-open", 10_000, 200)))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results.Select(result => result.CashAccountId).Distinct());
        Assert.Single(ledger.GetJournals(run));
        Assert.Equal(10_000, ledger.GetBalance(run, results[0].CashAccountId));
    }

    [Fact]
    public void Repeated_idempotency_key_returns_original_commit_without_duplicate_effect()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 50, 20).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        var request = new JournalRequest(
            run,
            "same-key",
            JournalKind.Transfer,
            "transfer",
            [new(cash, -10), new(merchant, 10)]);

        var first = ledger.Commit(request);
        var retry = ledger.Commit(request);

        Assert.True(first.Committed);
        Assert.True(retry.WasIdempotentReplay);
        Assert.Equal(first.JournalId, retry.JournalId);
        Assert.Equal(40, ledger.GetBalance(run, cash));
        Assert.Equal(10, ledger.GetBalance(run, merchant));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    [Fact]
    public void Reject_conflicting_payload_for_existing_idempotency_key()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 50, 20).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        ledger.Commit(new JournalRequest(
            run,
            "same-key",
            JournalKind.Transfer,
            "transfer",
            [new(cash, -10), new(merchant, 10)]));

        var conflicting = new JournalRequest(
            run,
            "same-key",
            JournalKind.Transfer,
            "transfer",
            [new(cash, -11), new(merchant, 11)]);

        Assert.Throws<IdempotencyConflictException>(() =>
            ledger.Commit(conflicting));

        Assert.Equal(40, ledger.GetBalance(run, cash));
        Assert.Equal(10, ledger.GetBalance(run, merchant));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    [Fact]
    public void Reject_cross_run_account_postings()
    {
        var ledger = new InMemoryLedger();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        var cashA = ledger.CreateAccount(runA, AccountKind.AgentCash);
        var accountB = ledger.CreateAccount(runB, AccountKind.Merchant);
        Assert.Throws<LedgerValidationException>(() => ledger.Commit(new JournalRequest(runA, "cross-run", JournalKind.Transfer, "transfer", [new PostingRequest(cashA, -10), new PostingRequest(accountB, 10)])));
        Assert.Equal(0, ledger.GetBalance(runA, cashA));
        Assert.Equal(0, ledger.GetBalance(runB, accountB));
    }

    [Fact]
    public void Transfer_enforces_limit_positive_amount_and_available_balance()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 100, 50).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(run, "too-large", cash, merchant, 51));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(run, "zero", cash, merchant, 0));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(run, "negative", cash, merchant, -1));

        Assert.Equal(100, ledger.GetBalance(run, cash));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
        Assert.Single(ledger.GetJournals(run));

        var otherRun = Guid.NewGuid();
        var otherCash = ledger.OpenRun(
            otherRun, "open", 100, 200).CashAccountId;
        var otherMerchant = ledger.CreateAccount(
            otherRun, AccountKind.Merchant);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(
                otherRun, "insufficient", otherCash, otherMerchant, 101));

        Assert.Equal(100, ledger.GetBalance(otherRun, otherCash));
        Assert.Equal(0, ledger.GetBalance(otherRun, otherMerchant));
        Assert.Single(ledger.GetJournals(otherRun));
    }

    [Fact]
    public void Savings_move_only_moves_between_agent_cash_and_savings()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 200, 100).CashAccountId;
        var savings = ledger.CreateAccount(run, AccountKind.AgentSavings);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        ledger.MoveToSavings(run, "save-1", cash, savings, 75);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.MoveToSavings(
                run, "save-wrong-account", cash, merchant, 10));

        Assert.Equal(125, ledger.GetBalance(run, cash));
        Assert.Equal(75, ledger.GetBalance(run, savings));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    [Fact]
    public void Bill_payment_requires_server_approved_amount_and_merchant_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 300, 200).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        var savings = ledger.CreateAccount(run, AccountKind.AgentSavings);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayBill(run, "wrong-amount", cash, merchant, 100, 120));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayBill(run, "wrong-payee", cash, savings, 120, 120));

        var paid = ledger.PayBill(
            run, "bill-1", cash, merchant, 120, 120);

        Assert.True(paid.Committed);
        Assert.Equal(180, ledger.GetBalance(run, cash));
        Assert.Equal(120, ledger.GetBalance(run, merchant));
        Assert.Equal(0, ledger.GetBalance(run, savings));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    [Fact]
    public void Run_initialization_retry_returns_same_cash_account_and_rejects_conflicting_reopen()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var first = ledger.OpenRun(run, "open-1", 10_000, 200);
        var retry = ledger.OpenRun(run, "open-1", 10_000, 200);
        Assert.Equal(first.CashAccountId, retry.CashAccountId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.Single(ledger.GetJournals(run));
        Assert.Throws<IdempotencyConflictException>(() => ledger.OpenRun(run, "open-2", 10_000, 200));
        Assert.Equal(10_000, ledger.GetBalance(run, first.CashAccountId));
    }

    [Fact]
    public void Transfer_rejects_treasury_as_source_without_effect()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 100, 10).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        var opening = Assert.Single(ledger.GetJournals(run));
        var treasury = opening.Postings.Single(
            posting => posting.AmountSen < 0).AccountId;

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(run, "invalid-source", treasury, merchant, 1));

        Assert.Single(ledger.GetJournals(run));
        Assert.Equal(100, ledger.GetBalance(run, cash));
        Assert.Equal(-100, ledger.GetBalance(run, treasury));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
    }

    [Fact]
    public void Bill_payment_uses_run_scoped_server_obligation_and_is_idempotent()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        var cash = ledger.OpenRun(run, "open", 500, 200).CashAccountId;
        var otherCash = ledger.OpenRun(
            otherRun, "open", 100, 200).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        var obligation = ledger.CreateObligation(
            run, merchant, 125, "INV-MEMORY-001");

        Assert.Throws<LedgerValidationException>(() =>
            ledger.GetObligation(otherRun, obligation.ObligationId));

        var paid = ledger.PayObligation(
            run, "bill-key", cash, obligation.ObligationId);
        var retry = ledger.PayObligation(
            run, "bill-key", cash, obligation.ObligationId);

        Assert.Equal(paid.JournalId, retry.JournalId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.True(ledger.GetObligation(run, obligation.ObligationId).Paid);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayObligation(
                run, "different-key", cash, obligation.ObligationId));

        Assert.Equal(375, ledger.GetBalance(run, cash));
        Assert.Equal(125, ledger.GetBalance(run, merchant));
        Assert.Equal(100, ledger.GetBalance(otherRun, otherCash));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    [Fact]
    public async Task Concurrent_spends_cannot_overdraw_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 100, 100).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        var attempts = Enumerable.Range(0, 20).Select(index =>
            Task.Run(() => ledger.TrySpend(
                run, $"spend-{index}", cash, merchant, 10)));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(10, results.Count(result => result));
        Assert.Equal(0, ledger.GetBalance(run, cash));
        Assert.Equal(100, ledger.GetBalance(run, merchant));
        Assert.Equal(11, ledger.GetJournals(run).Count);

        Assert.All(ledger.GetJournals(run), journal =>
            Assert.Equal(
                0L, journal.Postings.Sum(posting => posting.AmountSen)));
    }

    [Fact]
    public void Duplicate_invoice_has_one_obligation_and_one_payment()
    {
        var ledger = new InMemoryLedger();
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(runId, AccountKind.Merchant);
        var otherMerchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var first = ledger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        var retry = ledger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        Assert.Equal(first, retry);

        Assert.Throws<IdempotencyConflictException>(() =>
            ledger.CreateObligation(
                runId, merchantId, 121, "INV-DUPLICATE-001"));

        Assert.Throws<IdempotencyConflictException>(() =>
            ledger.CreateObligation(
                runId, otherMerchantId, 120, "INV-DUPLICATE-001"));

        Assert.Equal(first, ledger.GetObligation(runId, first.ObligationId));
        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Single(ledger.GetJournals(runId));

        var payment = ledger.PayObligation(
            runId, "payment", cashId, first.ObligationId);

        var afterPayment = ledger.CreateObligation(
            runId, merchantId, 120, "INV-DUPLICATE-001");

        Assert.Equal(first.ObligationId, afterPayment.ObligationId);
        Assert.True(afterPayment.Paid);

        var paymentRetry = ledger.PayObligation(
            runId, "payment", cashId, afterPayment.ObligationId);

        Assert.Equal(payment.JournalId, paymentRetry.JournalId);
        Assert.True(paymentRetry.WasIdempotentReplay);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayObligation(
                runId, "second-payment", cashId,
                afterPayment.ObligationId));

        Assert.Equal(880, ledger.GetBalance(runId, cashId));
        Assert.Equal(120, ledger.GetBalance(runId, merchantId));
        Assert.Equal(0, ledger.GetBalance(runId, otherMerchantId));
        Assert.Equal(2, ledger.GetJournals(runId).Count);
    }

    [Fact]
    public async Task Concurrent_invoice_delivery_creates_one_obligation()
    {
        var ledger = new InMemoryLedger();
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var requests = Enumerable.Range(0, 12).Select(index =>
            Task.Run(() => ledger.CreateObligation(
                runId, merchantId, 120, "INV-CONCURRENT-001")));

        var results = await Task.WhenAll(requests);

        Assert.Single(results.Select(result => result.ObligationId).Distinct());
        Assert.All(results, result =>
        {
            Assert.Equal("INV-CONCURRENT-001", result.InvoiceReference);
            Assert.Equal(120, result.AmountSen);
            Assert.False(result.Paid);
        });

        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Single(ledger.GetJournals(runId));
    }

    [Fact]
    public void Invoice_references_are_validated_case_sensitive_and_run_scoped()
    {
        var ledger = new InMemoryLedger();
        var runId = Guid.NewGuid();
        var cashId = ledger.OpenRun(runId, "open", 1_000, 200).CashAccountId;
        var merchantId = ledger.CreateAccount(
            runId, AccountKind.Merchant);

        var invalidReferences = new string[]
        {
            null!,
            "",
            " ",
            " INV-001",
            "INV-001 ",
            "\tINV-001",
            "INV-001\n",
            "legacy:existing"
        };

        foreach (var reference in invalidReferences)
        {
            Assert.Throws<LedgerValidationException>(() =>
                ledger.CreateObligation(
                    runId, merchantId, 120, reference));
        }

        var first = ledger.CreateObligation(
            runId, merchantId, 120, "INV-SCOPED-001");

        var differentlyCased = ledger.CreateObligation(
            runId, merchantId, 120, "inv-scoped-001");

        Assert.NotEqual(first.ObligationId, differentlyCased.ObligationId);

        var otherRunId = Guid.NewGuid();
        var otherCashId = ledger.OpenRun(
            otherRunId, "open", 1_000, 200).CashAccountId;
        var otherMerchantId = ledger.CreateAccount(
            otherRunId, AccountKind.Merchant);

        var other = ledger.CreateObligation(
            otherRunId, otherMerchantId, 120, "INV-SCOPED-001");

        Assert.NotEqual(first.ObligationId, other.ObligationId);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.GetObligation(runId, other.ObligationId));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.GetObligation(otherRunId, first.ObligationId));

        Assert.Equal(1_000, ledger.GetBalance(runId, cashId));
        Assert.Equal(1_000, ledger.GetBalance(otherRunId, otherCashId));
        Assert.Equal(0, ledger.GetBalance(runId, merchantId));
        Assert.Equal(0, ledger.GetBalance(otherRunId, otherMerchantId));
        Assert.Single(ledger.GetJournals(runId));
        Assert.Single(ledger.GetJournals(otherRunId));
    }

    // Memastikan cap x blh diubah melalui retry OpenRun & funding awal blh melebihi cap action
    [Fact]
    public void Run_transaction_policy_is_required_and_immutable() {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();

        Assert.Throws<LedgerValidationException>(() => ledger.OpenRun(run, "open", 1_000, 0));
        Assert.Throws<LedgerValidationException>(() => ledger.OpenRun(run, "open", 1_000, -1));

        Assert.Empty(ledger.GetJournals(run));

        var first = ledger.OpenRun(run, "open", 1_000, 100);
        var retry = ledger.OpenRun(run, "open", 1_000, 100);

        Assert.Equal(first.JournalId, retry.JournalId);
        Assert.Equal(first.CashAccountId, retry.CashAccountId);
        Assert.True(retry.WasIdempotentReplay);

        Assert.Throws<IdempotencyConflictException>(() => ledger.OpenRun(run, "open", 1_000, 101));
        Assert.Throws<IdempotencyConflictException>(() => ledger.OpenRun(run, "open", 1_000, 99));

        Assert.Equal(1_000, ledger.GetBalance(run, first.CashAccountId));
        Assert.Single(ledger.GetJournals(run));
    }

    // Memastikan semua laluan mutation menggunakan cap yg sama,include Commit & TrySpend
    [Fact]
    public void Stored_cap_applies_to_every_money_mutation_path()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.OpenRun(run, "open", 1_000, 100).CashAccountId;
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        var savings = ledger.CreateAccount(run, AccountKind.AgentSavings);

        var obligation = ledger.CreateObligation(
            run, merchant, 101, "INV-OVER-CAP");

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(run, "transfer", cash, merchant, 101));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.MoveToSavings(run, "savings", cash, savings, 101));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayBill(run, "bill", cash, merchant, 101, 101));

        Assert.Throws<LedgerValidationException>(() =>
            ledger.PayObligation(
                run, "obligation", cash, obligation.ObligationId));

        var directRequest = new JournalRequest(
            run,
            "direct",
            JournalKind.Transfer,
            "direct transfer",
            [new(cash, -101), new(merchant, 101)]);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Commit(directRequest));

        Assert.False(ledger.TrySpend(
            run, "helper", cash, merchant, 101));

        Assert.Equal(1_000, ledger.GetBalance(run, cash));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
        Assert.Equal(0, ledger.GetBalance(run, savings));
        Assert.False(ledger.GetObligation(run, obligation.ObligationId).Paid);
        Assert.Single(ledger.GetJournals(run));

        var allowed = ledger.Transfer(
            run, "at-cap", cash, merchant, 100);

        var replay = ledger.Transfer(
            run, "at-cap", cash, merchant, 100);

        Assert.Equal(allowed.JournalId, replay.JournalId);
        Assert.True(replay.WasIdempotentReplay);
        Assert.Equal(900, ledger.GetBalance(run, cash));
        Assert.Equal(100, ledger.GetBalance(run, merchant));
        Assert.Equal(2, ledger.GetJournals(run).Count);
    }

    // Journal yg sah dr segi bentuk tetap ditolak jika run belum ada policy
    [Fact]
    public void Direct_commits_without_run_policy_fail_closed()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var treasury = ledger.CreateAccount(
            run, AccountKind.SimulatorTreasury);
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);

        var opening = new JournalRequest(
            run,
            "direct-opening",
            JournalKind.OpeningFunding,
            "opening without policy",
            [new(treasury, -100), new(cash, 100)]);

        var fundingError = Assert.Throws<LedgerValidationException>(() =>
            ledger.Commit(opening));

        Assert.Equal(
            "Run transaction policy is not configured.",
            fundingError.Message);

        var transfer = new JournalRequest(
            run,
            "direct-transfer",
            JournalKind.Transfer,
            "transfer without policy",
            [new(cash, -1), new(merchant, 1)]);

        var transferError = Assert.Throws<LedgerValidationException>(() =>
            ledger.Commit(transfer));

        Assert.Equal(
            "Run transaction policy is not configured.",
            transferError.Message);

        Assert.Equal(0, ledger.GetBalance(run, treasury));
        Assert.Equal(0, ledger.GetBalance(run, cash));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
        Assert.Empty(ledger.GetJournals(run));
    }

    // Setiap run menggunakan cap sendiri
    [Fact]
    public void Transaction_caps_are_isolated_between_runs()
    {
        var ledger = new InMemoryLedger();
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();

        var firstCash = ledger.OpenRun(
            firstRun, "open", 1_000, 50).CashAccountId;
        var secondCash = ledger.OpenRun(
            secondRun, "open", 1_000, 200).CashAccountId;

        var firstMerchant = ledger.CreateAccount(
            firstRun, AccountKind.Merchant);
        var secondMerchant = ledger.CreateAccount(
            secondRun, AccountKind.Merchant);

        Assert.Throws<LedgerValidationException>(() =>
            ledger.Transfer(
                firstRun, "transfer", firstCash, firstMerchant, 101));

        var allowed = ledger.Transfer(
            secondRun, "transfer", secondCash, secondMerchant, 101);

        Assert.True(allowed.Committed);
        Assert.Equal(1_000, ledger.GetBalance(firstRun, firstCash));
        Assert.Equal(0, ledger.GetBalance(firstRun, firstMerchant));
        Assert.Equal(899, ledger.GetBalance(secondRun, secondCash));
        Assert.Equal(101, ledger.GetBalance(secondRun, secondMerchant));
        Assert.Single(ledger.GetJournals(firstRun));
        Assert.Equal(2, ledger.GetJournals(secondRun).Count);
    }
}
