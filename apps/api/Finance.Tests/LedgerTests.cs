using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

public sealed class LedgerTests
{
    [Fact]
    public void Commit_balanced_entry_moves_virtual_funds()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var result = ledger.Commit(new JournalRequest(run, "opening-1", JournalKind.OpeningFunding, "opening", [new PostingRequest(treasury, -100_000), new PostingRequest(cash, 100_000)]));
        Assert.True(result.Committed);
        Assert.Equal(100_000, ledger.GetBalance(run, cash));
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
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var first = new JournalRequest(run, "opening-first", JournalKind.OpeningFunding, "opening", [new(treasury, -100), new(cash, 100)]);
        var second = new JournalRequest(run, "opening-second", JournalKind.OpeningFunding, "opening", [new(treasury, -200), new(cash, 200)]);

        ledger.Commit(first);
        Assert.Throws<LedgerValidationException>(() => ledger.Commit(second));
        Assert.Equal(100, ledger.GetBalance(run, cash));
        Assert.Single(ledger.GetJournals(run));
    }

    [Fact]
    public async Task Concurrent_run_initialization_creates_one_opening_journal_and_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => ledger.OpenRun(run, "concurrent-open", 10_000)))
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
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var request = new JournalRequest(run, "same-key", JournalKind.OpeningFunding, "opening", [new PostingRequest(treasury, -50), new PostingRequest(cash, 50)]);
        var first = ledger.Commit(request);
        var retry = ledger.Commit(request);
        Assert.True(first.Committed);
        Assert.True(retry.WasIdempotentReplay);
        Assert.Equal(first.JournalId, retry.JournalId);
        Assert.Equal(50, ledger.GetBalance(run, cash));
        Assert.Single(ledger.GetJournals(run));
    }

    [Fact]
    public void Reject_conflicting_payload_for_existing_idempotency_key()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        ledger.Commit(new JournalRequest(run, "same-key", JournalKind.OpeningFunding, "opening", [new PostingRequest(treasury, -50), new PostingRequest(cash, 50)]));
        Assert.Throws<IdempotencyConflictException>(() => ledger.Commit(new JournalRequest(run, "same-key", JournalKind.OpeningFunding, "opening", [new PostingRequest(treasury, -60), new PostingRequest(cash, 60)])));
        Assert.Equal(50, ledger.GetBalance(run, cash));
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
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        ledger.Commit(new JournalRequest(run, "fund-transfer-test", JournalKind.OpeningFunding, "opening", [new(treasury, -100), new(cash, 100)]));

        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(run, "too-large", cash, merchant, 51, 50));
        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(run, "zero", cash, merchant, 0, 50));
        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(run, "insufficient", cash, merchant, 101, 200));
        Assert.Equal(100, ledger.GetBalance(run, cash));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
    }

    [Fact]
    public void Savings_move_only_moves_between_agent_cash_and_savings()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var savings = ledger.CreateAccount(run, AccountKind.AgentSavings);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        ledger.Commit(new JournalRequest(run, "fund-savings-test", JournalKind.OpeningFunding, "opening", [new(treasury, -200), new(cash, 200)]));

        ledger.MoveToSavings(run, "save-1", cash, savings, 75, 100);
        Assert.Equal(125, ledger.GetBalance(run, cash));
        Assert.Equal(75, ledger.GetBalance(run, savings));
        Assert.Throws<LedgerValidationException>(() => ledger.MoveToSavings(run, "save-wrong-account", cash, treasury, 10, 100));
    }

    [Fact]
    public void Bill_payment_requires_server_approved_amount_and_merchant_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        ledger.Commit(new JournalRequest(run, "fund-bill-test", JournalKind.OpeningFunding, "opening", [new(treasury, -300), new(cash, 300)]));

        Assert.Throws<LedgerValidationException>(() => ledger.PayBill(run, "wrong-amount", cash, merchant, 100, 120, 200));
        var paid = ledger.PayBill(run, "bill-1", cash, merchant, 120, 120, 200);
        Assert.True(paid.Committed);
        Assert.Equal(180, ledger.GetBalance(run, cash));
        Assert.Equal(120, ledger.GetBalance(run, merchant));
    }

    [Fact]
    public void Run_initialization_retry_returns_same_cash_account_and_rejects_conflicting_reopen()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var first = ledger.OpenRun(run, "open-1", 10_000);
        var retry = ledger.OpenRun(run, "open-1", 10_000);
        Assert.Equal(first.CashAccountId, retry.CashAccountId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.Single(ledger.GetJournals(run));
        Assert.Throws<IdempotencyConflictException>(() => ledger.OpenRun(run, "open-2", 10_000));
        Assert.Equal(10_000, ledger.GetBalance(run, first.CashAccountId));
    }

    [Fact]
    public void Transfer_rejects_treasury_as_source_without_effect()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        Assert.Throws<LedgerValidationException>(() => ledger.Transfer(run, "invalid-source", treasury, merchant, 1, 10));
        Assert.Empty(ledger.GetJournals(run));
        Assert.Equal(0, ledger.GetBalance(run, treasury));
        Assert.Equal(0, ledger.GetBalance(run, merchant));
    }

    [Fact]
    public void Bill_payment_uses_run_scoped_server_obligation_and_is_idempotent()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        var otherMerchant = ledger.CreateAccount(otherRun, AccountKind.Merchant);
        ledger.Commit(new JournalRequest(run, "fund-obligation-test", JournalKind.OpeningFunding, "opening", [new(treasury, -500), new(cash, 500)]));
        var obligation = ledger.CreateObligation(run, merchant, 125);

        Assert.Throws<LedgerValidationException>(() => ledger.GetObligation(otherRun, obligation.ObligationId));
        var paid = ledger.PayObligation(run, "bill-key", cash, obligation.ObligationId, 200);
        var retry = ledger.PayObligation(run, "bill-key", cash, obligation.ObligationId, 200);
        Assert.Equal(paid.JournalId, retry.JournalId);
        Assert.True(retry.WasIdempotentReplay);
        Assert.True(ledger.GetObligation(run, obligation.ObligationId).Paid);
        Assert.Equal(375, ledger.GetBalance(run, cash));
        Assert.Equal(125, ledger.GetBalance(run, merchant));
        Assert.Throws<LedgerValidationException>(() => ledger.PayObligation(run, "different-key", cash, obligation.ObligationId, 200));
        Assert.Equal(0, ledger.GetBalance(otherRun, otherMerchant));
    }

    [Fact]
    public async Task Concurrent_spends_cannot_overdraw_account()
    {
        var ledger = new InMemoryLedger();
        var run = Guid.NewGuid();
        var cash = ledger.CreateAccount(run, AccountKind.AgentCash);
        var treasury = ledger.CreateAccount(run, AccountKind.SimulatorTreasury);
        var merchant = ledger.CreateAccount(run, AccountKind.Merchant);
        ledger.Commit(new JournalRequest(run, "fund", JournalKind.OpeningFunding, "opening", [new PostingRequest(treasury, -100), new PostingRequest(cash, 100)]));
        var outcomes = Enumerable.Range(0, 20).Select(index => Task.Run(() =>
        {
            try { return ledger.TrySpend(run, $"spend-{index}", cash, merchant, 10); }
            catch (LedgerValidationException) { return false; }
        })).ToArray();
        var results = await Task.WhenAll(outcomes);
        Assert.Equal(10, results.Count(result => result));
        Assert.Equal(0, ledger.GetBalance(run, cash));
        Assert.Equal(100, ledger.GetBalance(run, merchant));
        Assert.All(ledger.GetJournals(run), journal => Assert.Equal(0, journal.Postings.Sum(p => p.AmountSen)));
    }
}
