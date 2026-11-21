using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// Phase 1 exit gate: concurrency, overflow, and seeded randomized invariant/differential tests.
public sealed class Phase1GateTests
{
    private static NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_TEST_DATABASE")!);

    // Gap 1: one obligation, many concurrent payments with different keys → exactly one payment.

    [Fact]
    public Task InMemory_concurrent_payments_pay_once() => AssertConcurrentPaymentsPayOnce(new InMemoryLedger());

    [PostgresFact]
    public async Task Postgres_concurrent_payments_pay_once()
    {
        using var dataSource = OwnerDataSource();
        await AssertConcurrentPaymentsPayOnce(new PostgresLedger(dataSource));
    }

    private static async Task AssertConcurrentPaymentsPayOnce(IFinanceLedger ledger)
    {
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
        var obligation = ledger.CreateObligation(runId, merchant, 200, "INV-RACE");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            try { return (Code: (LedgerReasonCode?)null, Result: ledger.PayObligation(runId, $"pay-{index}", cash, obligation.ObligationId)); }
            catch (LedgerException ex) { return (Code: (LedgerReasonCode?)ex.ReasonCode, Result: (CommitResult?)null); }
        })));

        var winner = Assert.Single(outcomes, o => o.Code is null);
        Assert.All(outcomes.Where(o => o.Code is not null), o => Assert.Equal(LedgerReasonCode.ObligationAlreadyPaid, o.Code));
        Assert.Equal(800, ledger.GetBalance(runId, cash));
        Assert.Equal(200, ledger.GetBalance(runId, merchant));
        Assert.Single(ledger.GetJournals(runId), j => j.Kind == JournalKind.BillPayment);
        Assert.True(ledger.GetObligation(runId, obligation.ObligationId).Paid);

        var events = ledger.GetEvents(runId, 0, RunEventPaging.MaxLimit);
        Assert.Single(events, e => e.EventType == "OBLIGATION_PAID" && e.JournalId == winner.Result!.JournalId);
        Assert.Equal(11, events.Count(e => e.EventType == "POLICY_DENIED" && e.ReasonCode == "OBLIGATION_ALREADY_PAID"));
    }

    // Gap 2: a new payment racing the reversal of the old payment ends in one of the two serial outcomes.

    [Fact]
    public Task InMemory_payment_racing_reversal_stays_consistent() => AssertPaymentRacingReversal(new InMemoryLedger());

    [PostgresFact]
    public async Task Postgres_payment_racing_reversal_stays_consistent()
    {
        using var dataSource = OwnerDataSource();
        await AssertPaymentRacingReversal(new PostgresLedger(dataSource), dataSource);
    }

    private static async Task AssertPaymentRacingReversal(IFinanceLedger ledger, NpgsqlDataSource? dataSource = null)
    {
        var sawReverseFirst = false;
        var sawPayFirst = false;

        for (var round = 0; round < 15; round++)
        {
            var runId = Guid.NewGuid();
            var cash = ledger.OpenRun(runId, "open", 1_000, 500).CashAccountId;
            var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);
            var obligation = ledger.CreateObligation(runId, merchant, 200, "INV-1");
            var first = ledger.PayObligation(runId, "pay-1", cash, obligation.ObligationId);

            using var start = new Barrier(2);
            var reverse = Task.Run(() =>
            {
                start.SignalAndWait();
                ledger.ReverseJournal(runId, "rev-1", first.JournalId, "bounced", ReversalInitiator.ScenarioEngine);
            });
            var pay = Task.Run(() =>
            {
                start.SignalAndWait();
                try { ledger.PayObligation(runId, "pay-2", cash, obligation.ObligationId); return true; }
                catch (LedgerException ex) { Assert.Equal(LedgerReasonCode.ObligationAlreadyPaid, ex.ReasonCode); return false; }
            });
            await reverse;
            var secondPaid = await pay;

            // Reverse first → pay-2 succeeds (PAID, 800). Pay first → pay-2 denied, then reversal (UNPAID, 1000).
            Assert.Equal(secondPaid, ledger.GetObligation(runId, obligation.ObligationId).Paid);
            Assert.Equal(secondPaid ? 800 : 1_000, ledger.GetBalance(runId, cash));
            if (secondPaid) sawReverseFirst = true; else sawPayFirst = true;
            Invariants.AssertRun(ledger, runId, [cash], [obligation.ObligationId]);
            if (dataSource is not null) Assert.Null(VerifyChain(dataSource, runId));
        }

        // At least one ordering was exercised; both may not occur on every machine.
        Assert.True(sawReverseFirst || sawPayFirst);
    }

    // Gap 4: amounts at the edge of the signed 64-bit range never overflow silently.

    [Fact]
    public void InMemory_extreme_amounts_are_safe() => AssertExtremeAmounts(new InMemoryLedger());

    [PostgresFact]
    public void Postgres_extreme_amounts_are_safe()
    {
        using var dataSource = OwnerDataSource();
        AssertExtremeAmounts(new PostgresLedger(dataSource));
    }

    private static void AssertExtremeAmounts(IFinanceLedger ledger)
    {
        var runId = Guid.NewGuid();
        var cash = ledger.OpenRun(runId, "open", long.MaxValue, long.MaxValue).CashAccountId;
        var merchant = ledger.CreateAccount(runId, AccountKind.Merchant);

        ledger.Transfer(runId, "all", cash, merchant, long.MaxValue);
        Assert.Equal(0, ledger.GetBalance(runId, cash));
        Assert.Equal(long.MaxValue, ledger.GetBalance(runId, merchant));

        var insufficient = Assert.ThrowsAny<LedgerException>(() => ledger.Transfer(runId, "again", cash, merchant, long.MaxValue));
        Assert.Equal(LedgerReasonCode.InsufficientFunds, insufficient.ReasonCode);
        var negative = Assert.ThrowsAny<LedgerException>(() => ledger.Transfer(runId, "min", cash, merchant, long.MinValue));
        Assert.Equal(LedgerReasonCode.InvalidAmount, negative.ReasonCode);

        // Two positive maximum postings overflow the balance check; this is a malformed amount, not a crash.
        var overflowing = new JournalRequest(runId, "overflow", JournalKind.Transfer, "overflow",
            [new(cash, long.MaxValue), new(merchant, long.MaxValue)]);
        var overflow = Assert.ThrowsAny<LedgerException>(() => Commit(ledger, overflowing));
        Assert.Equal(LedgerReasonCode.InvalidAmount, overflow.ReasonCode);

        Assert.Equal(0, ledger.GetBalance(runId, cash));
        Assert.Equal(long.MaxValue, ledger.GetBalance(runId, merchant));
        Assert.Equal(3, ledger.GetEvents(runId, 0, RunEventPaging.MaxLimit).Count(e => e.EventType == "POLICY_DENIED"));
        Invariants.AssertRun(ledger, runId, [cash], []);
    }

    private static CommitResult Commit(IFinanceLedger ledger, JournalRequest request) => ledger switch
    {
        InMemoryLedger memory => memory.Commit(request),
        PostgresLedger postgres => postgres.Commit(request),
        _ => throw new InvalidOperationException()
    };

    // Gap 3: seeded random operation sequences. Same seed → same sequence, so failures are reproducible.

    public static TheoryData<int> InMemorySeeds() => new(Enumerable.Range(1, 25));
    public static TheoryData<int> DifferentialSeeds() => new(Enumerable.Range(101, 6));

    [Theory]
    [MemberData(nameof(InMemorySeeds))]
    public void InMemory_random_operations_keep_invariants(int seed) =>
        RandomOperations.Run(seed, steps: 300, new InMemoryLedger());

    [PostgresTheory]
    [MemberData(nameof(DifferentialSeeds))]
    public void Postgres_and_in_memory_agree_on_random_operations(int seed)
    {
        using var dataSource = OwnerDataSource();
        var runIds = RandomOperations.Run(seed, steps: 150, new InMemoryLedger(), new PostgresLedger(dataSource));
        Assert.Null(VerifyChain(dataSource, runIds[1]));
    }

    private static long? VerifyChain(NpgsqlDataSource dataSource, Guid runId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT finmind_verify_run_events(@run_id)", connection);
        command.Parameters.AddWithValue("run_id", runId);
        return command.ExecuteScalar() is long sequence ? sequence : null;
    }

    private static class Invariants
    {
        public static void AssertRun(IFinanceLedger ledger, Guid runId, IReadOnlyList<Guid> agentAccounts, IReadOnlyList<Guid> obligations)
        {
            var journals = ledger.GetJournals(runId);
            var postings = journals.SelectMany(j => j.Postings).ToArray();

            // Every journal balances, and so does the whole run.
            Assert.All(journals, j => Assert.Equal(0, j.Postings.Sum(p => (decimal)p.AmountSen)));
            Assert.Equal(0m, postings.Sum(p => (decimal)p.AmountSen));

            // Balances are the sum of immutable postings; agent accounts never go negative.
            foreach (var account in postings.GroupBy(p => p.AccountId))
                Assert.Equal(account.Sum(p => (decimal)p.AmountSen), ledger.GetBalance(runId, account.Key));
            Assert.All(agentAccounts, account => Assert.True(ledger.GetBalance(runId, account) >= 0));

            // An obligation is PAID exactly when it has one unreversed payment.
            var reversed = journals.Where(j => j.ReversesJournalId is not null).Select(j => j.ReversesJournalId!.Value).ToHashSet();
            Assert.Equal(reversed.Count, journals.Count(j => j.Kind == JournalKind.Reversal));
            foreach (var obligationId in obligations)
            {
                var effective = journals.Count(j => j.Kind == JournalKind.BillPayment && j.ObligationId == obligationId && !reversed.Contains(j.JournalId));
                Assert.InRange(effective, 0, 1);
                Assert.Equal(effective == 1, ledger.GetObligation(runId, obligationId).Paid);
            }

            // Events: gap-free sequence, one LEDGER_COMMITTED per journal.
            var events = ledger.GetEvents(runId, 0, RunEventPaging.MaxLimit);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
            Assert.Equal(journals.Select(j => j.JournalId).Order(),
                events.Where(e => e.EventType == "LEDGER_COMMITTED").Select(e => e.JournalId!.Value).Order());
        }
    }

    // Generates operations against logical names so ledgers with different generated IDs can be compared.
    private static class RandomOperations
    {
        private sealed class State(IFinanceLedger ledger)
        {
            public IFinanceLedger Ledger { get; } = ledger;
            public Guid RunId;
            public readonly Dictionary<string, Guid> Accounts = [];
            public readonly List<Guid> Obligations = [];
            public readonly List<Guid> Journals = [];
            public int Rejections;

            public int? JournalIndex(Guid journalId)
            {
                var index = Journals.IndexOf(journalId);
                if (index >= 0) return index;
                Journals.Add(journalId);
                return null;
            }

            public int ObligationIndex(Guid obligationId)
            {
                var index = Obligations.IndexOf(obligationId);
                if (index >= 0) return index;
                Obligations.Add(obligationId);
                return Obligations.Count - 1;
            }
        }

        private static readonly string[] Sources = ["cash", "savings", "merchant", "unknown"];
        private static readonly string[] Destinations = ["merchant", "merchant2", "savings", "cash", "other_run_cash", "unknown"];
        private static readonly long[] Amounts = [-5, 0, 1, 25, 60, 100, 150, 151, 900];

        public static Guid[] Run(int seed, int steps, params IFinanceLedger[] ledgers)
        {
            var states = ledgers.Select(ledger => Initialize(new State(ledger))).ToArray();
            var random = new Random(seed);

            for (var step = 0; step < steps; step++)
            {
                var operation = random.Next(6);
                var source = Sources[random.Next(Sources.Length)];
                var destination = Destinations[random.Next(Destinations.Length)];
                var amount = Amounts[random.Next(Amounts.Length)];
                var key = $"k-{random.Next(40)}";
                var pick = random.Next(1_000);
                var invoice = $"INV-{random.Next(6)}";
                var reason = random.Next(2) == 0 ? "bounced" : "correction";

                var outcomes = states.Select(state =>
                    Apply(state, operation, source, destination, amount, key, pick, invoice, reason)).ToArray();

                var context = $"seed {seed}, step {step}, operation {operation}";
                foreach (var outcome in outcomes.Skip(1))
                    Assert.True(outcomes[0] == outcome, $"{context}: {outcomes[0]} vs {outcome}");

                foreach (var state in states)
                {
                    Invariants.AssertRun(state.Ledger, state.RunId,
                        [state.Accounts["cash"], state.Accounts["savings"]], state.Obligations);
                    Assert.Equal(state.Rejections,
                        state.Ledger.GetEvents(state.RunId, 0, RunEventPaging.MaxLimit).Count(e => e.EventType == "POLICY_DENIED"));
                }

                if (states.Length > 1)
                {
                    foreach (var name in new[] { "cash", "savings", "merchant", "merchant2" })
                        Assert.True(states.Select(s => s.Ledger.GetBalance(s.RunId, s.Accounts[name])).Distinct().Count() == 1,
                            $"{context}: balance of {name} differs");
                }
            }

            return states.Select(s => s.RunId).ToArray();
        }

        private static State Initialize(State state)
        {
            var ledger = state.Ledger;
            state.RunId = Guid.NewGuid();
            var opening = ledger.OpenRun(state.RunId, "open", 1_000, 150);
            state.Accounts["cash"] = opening.CashAccountId;
            state.Accounts["savings"] = ledger.CreateAccount(state.RunId, AccountKind.AgentSavings);
            state.Accounts["merchant"] = ledger.CreateAccount(state.RunId, AccountKind.Merchant);
            state.Accounts["merchant2"] = ledger.CreateAccount(state.RunId, AccountKind.Merchant);
            state.Accounts["other_run_cash"] = ledger.OpenRun(Guid.NewGuid(), "open", 1_000, 150).CashAccountId;
            state.Accounts["unknown"] = Guid.NewGuid();
            state.Journals.Add(opening.JournalId);
            return state;
        }

        // Returns a ledger-independent description of the outcome.
        private static string Apply(State state, int operation, string source, string destination, long amount,
            string key, int pick, string invoice, string reason)
        {
            var ledger = state.Ledger;
            var run = state.RunId;
            try
            {
                switch (operation)
                {
                    case 0:
                        return Describe(state, ledger.Transfer(run, key, state.Accounts[source], state.Accounts[destination], amount));
                    case 1:
                        return Describe(state, ledger.MoveToSavings(run, key, state.Accounts[source], state.Accounts["savings"], amount));
                    case 2:
                    {
                        var payee = pick % 4 == 0 ? state.Accounts["cash"] : state.Accounts[pick % 2 == 0 ? "merchant" : "merchant2"];
                        var created = ledger.CreateObligation(run, payee, Math.Abs(amount) + 20, invoice);
                        return $"obligation {state.ObligationIndex(created.ObligationId)} paid={created.Paid} amount={created.AmountSen}";
                    }
                    case 3:
                    {
                        var obligation = state.Obligations.Count == 0 || pick % 7 == 0
                            ? Guid.NewGuid()
                            : state.Obligations[pick % state.Obligations.Count];
                        return Describe(state, ledger.PayObligation(run, key, state.Accounts[source], obligation));
                    }
                    case 4:
                    case 5:
                    {
                        var journal = pick % 9 == 0 ? Guid.NewGuid() : state.Journals[pick % state.Journals.Count];
                        var initiator = pick % 2 == 0 ? ReversalInitiator.Researcher : ReversalInitiator.ScenarioEngine;
                        return Describe(state, ledger.ReverseJournal(run, key, journal, reason, initiator));
                    }
                    default:
                        throw new InvalidOperationException();
                }
            }
            catch (LedgerException exception)
            {
                state.Rejections++;
                return $"rejected {exception.ReasonCode}";
            }
        }

        private static string Describe(State state, CommitResult result)
        {
            var existing = state.JournalIndex(result.JournalId);
            var reversedBy = result.ReversedByJournalId is { } id ? state.Journals.IndexOf(id).ToString() : "none";
            return $"journal {(existing?.ToString() ?? $"new {state.Journals.Count - 1}")} replay={result.WasIdempotentReplay} " +
                   $"effective={result.Effective} reversedBy={reversedBy}";
        }
    }
}
