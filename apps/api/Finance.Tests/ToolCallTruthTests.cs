using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Finmind.Api.Finance;
using Npgsql;

namespace Finmind.Finance.Tests;

// ADR-0013 item 16: a tool call is never recorded as rejected when it committed.
[Collection(QueuedRunTests.Name)]
public sealed class ToolCallTruthTests
{
    // Runs a hook immediately before or after one ledger method, to force an interleaving.
    public class Interleaved : DispatchProxy
    {
        private IFinanceLedger _target = null!;
        private Action<string, object?[]>? _before;
        private Action<string>? _after;

        public static IFinanceLedger Around(IFinanceLedger target, Action<string, object?[]>? before = null, Action<string>? after = null)
        {
            var proxy = Create<IFinanceLedger, Interleaved>();
            var self = (Interleaved)(object)proxy;
            (self._target, self._before, self._after) = (target, before, after);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            _before?.Invoke(method!.Name, args!);
            object? result;
            try { result = method!.Invoke(_target, args); }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
                throw;
            }
            _after?.Invoke(method.Name);
            return result;
        }
    }

    private const string CallId = "turn-0001";

    private static Guid RunningRun(IFinanceLedger ledger)
    {
        var run = ledger.CreateManagedRun(new JsonObject
        {
            ["objective"] = "Manage funds.", ["scenarioId"] = "S02", ["seed"] = 42, ["startingBalanceSen"] = 1_000_000,
            ["transactionLimitSen"] = 400_000, ["durationDays"] = 30, ["maxActions"] = 100, ["maxModelTokens"] = 0,
            ["maxWallTimeSeconds"] = 3_600, ["memoryMode"] = "none",
            ["agent"] = new JsonObject { ["provider"] = "mock", ["modelId"] = "finmind-mock-v1" }
        }, null).RunId;
        ledger.TransitionRun(run, RunStatus.Queued, null, "internal_api");
        ledger.TransitionRun(run, RunStatus.Initializing, null, FinanceEvents.WorkerSource);
        ledger.ActivateRun(run, FinanceEvents.WorkerSource);
        return run;
    }

    // The call was recorded and its effect committed; its completion never happened.
    private static T Interrupted<T>(IFinanceLedger ledger, Guid run, string tool, JsonObject args, Func<T> effect)
    {
        ledger.BeginToolCall(run, CallId, tool, (JsonObject)args.DeepClone(), ToolGateway.RequestHash(tool, args));
        using (FinanceCallContext.Begin(FinanceEvents.AgentSource, CallId)) return effect();
    }

    private static JsonObject Execute(IFinanceLedger ledger, Guid run, string tool, JsonObject args) =>
        new ToolGateway(ledger, TimeProvider.System).Execute(run, CallId, tool, args);

    private static void Cancel(IFinanceLedger ledger, Guid run) =>
        ledger.TransitionRun(run, RunStatus.Cancelled, "test", "internal_api");

    private static JsonObject Savings() => new() { ["amount_sen"] = 1_000, ["idempotency_key"] = "save" };

    private static CommitResult CommitSavings(IFinanceLedger ledger, Guid run)
    {
        var view = ledger.GetAgentView(run);
        return ledger.MoveToSavings(run, "agent:save", view.CashAccountId, view.SavingsAccountId!.Value, 1_000);
    }

    private static string? Recorded(IFinanceLedger ledger, Guid run) =>
        ledger.GetEvents(run, 0, 500).Single(e => e.EventType == "TOOL_RETURNED" && e.TraceId == CallId).Outcome;

    private static void AssertCommitted(IFinanceLedger ledger, Guid run, JsonObject response, CommitResult committed, bool effective = true)
    {
        Assert.Equal(("replayed", true, committed.JournalId.ToString(), effective),
            ((string)response["status"]!, (bool)response["committed"]!, (string?)response["journal_id"], (bool)response["effective"]!));
        Assert.Equal("replayed", Recorded(ledger, run));
    }

    private static readonly Dictionary<string, Action<IFinanceLedger>> Scenarios = new()
    {
        ["committed_savings_then_cancel"] = ledger =>
        {
            var run = RunningRun(ledger);
            var committed = Interrupted(ledger, run, "move_to_savings", Savings(), () => CommitSavings(ledger, run));
            Cancel(ledger, run);
            AssertCommitted(ledger, run, Execute(ledger, run, "move_to_savings", Savings()), committed);
        },
        ["committed_payment_then_cancel"] = ledger =>
        {
            var run = RunningRun(ledger);
            ledger.AdvanceClock(run, 1, "internal_api");
            var view = ledger.GetAgentView(run);
            var bill = view.Obligations[0].ObligationId;
            var args = new JsonObject { ["bill_id"] = bill.ToString(), ["idempotency_key"] = "pay" };
            var committed = Interrupted(ledger, run, "pay_bill", args,
                () => ledger.PayObligation(run, "agent:pay", view.CashAccountId, bill));
            Cancel(ledger, run);
            AssertCommitted(ledger, run, Execute(ledger, run, "pay_bill", args), committed);
        },
        ["committed_then_reversed_then_cancel"] = ledger =>
        {
            var run = RunningRun(ledger);
            var committed = Interrupted(ledger, run, "move_to_savings", Savings(), () => CommitSavings(ledger, run));
            ledger.ReverseJournal(run, "undo", committed.JournalId, "test", ReversalInitiator.Researcher);
            Cancel(ledger, run);
            AssertCommitted(ledger, run, Execute(ledger, run, "move_to_savings", Savings()), committed, effective: false);
        },
        ["clock_moved_then_cancel"] = ledger =>
        {
            var run = RunningRun(ledger);
            var args = new JsonObject { ["days"] = 3 };
            Interrupted(ledger, run, "wait", args, () => ledger.AdvanceClock(run, 3, FinanceEvents.AgentSource));
            Cancel(ledger, run);
            var response = Execute(ledger, run, "wait", args);
            Assert.Equal(("ok", 3, "ok"), ((string)response["status"]!, (int)response["data"]!["day"]!, Recorded(ledger, run)));
        },
        // The retry sees RUNNING, then the run is cancelled before the ledger is asked.
        ["cancel_between_the_check_and_the_ledger"] = ledger =>
        {
            var run = RunningRun(ledger);
            var committed = Interrupted(ledger, run, "move_to_savings", Savings(), () => CommitSavings(ledger, run));
            var racing = Interleaved.Around(ledger, after: method => { if (method == "BeginToolCall") Cancel(ledger, run); });
            AssertCommitted(ledger, run, Execute(racing, run, "move_to_savings", Savings()), committed);
        },
        // Two requests with one ID: the one that loses must not give the agent a different answer.
        ["response_is_the_one_that_was_recorded"] = ledger =>
        {
            var run = RunningRun(ledger);
            var first = ToolGateway.Rejected(LedgerReasonCode.InsufficientFunds);
            var racing = Interleaved.Around(ledger, before: (method, args) =>
            {
                if (method == "CompleteToolCall")
                    ledger.CompleteToolCall(run, CallId, "get_balance",
                        new ToolCallCompletion((JsonObject)first.DeepClone(), "denied", "INSUFFICIENT_FUNDS", null, null));
            });
            var response = Execute(racing, run, "get_balance", new JsonObject());
            Assert.Equal(("denied", "denied"), ((string)response["status"]!, Recorded(ledger, run)));
        },
        ["finished_call_cannot_commit_later"] = ledger =>
        {
            var run = RunningRun(ledger);
            ledger.BeginToolCall(run, CallId, "move_to_savings", Savings(), ToolGateway.RequestHash("move_to_savings", Savings()));
            ledger.CompleteToolCall(run, CallId, "move_to_savings", new ToolCallCompletion(
                ToolGateway.Rejected(LedgerReasonCode.InsufficientFunds), "denied", "INSUFFICIENT_FUNDS", null, null));
            var view = ledger.GetAgentView(run);
            var before = ledger.GetBalance(run, view.CashAccountId);
            using (FinanceCallContext.Begin(FinanceEvents.AgentSource, CallId))
                Assert.Equal(LedgerReasonCode.ToolCallIdConflict,
                    Assert.ThrowsAny<LedgerException>(() => CommitSavings(ledger, run)).ReasonCode);
            Assert.Equal(before, ledger.GetBalance(run, view.CashAccountId));
        },
        ["nothing_committed_then_cancel_is_still_denied"] = ledger =>
        {
            var run = RunningRun(ledger);
            ledger.BeginToolCall(run, CallId, "move_to_savings", Savings(), ToolGateway.RequestHash("move_to_savings", Savings()));
            Cancel(ledger, run);
            var response = Execute(ledger, run, "move_to_savings", Savings());
            Assert.Equal(("denied", false, "denied"), ((string)response["status"]!, (bool)response["committed"]!, Recorded(ledger, run)));
        },
    };

    public static TheoryData<string> ScenarioNames() => new(Scenarios.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void InMemory(string scenario) => Scenarios[scenario](new InMemoryLedger());

    [PostgresTheory]
    [MemberData(nameof(ScenarioNames))]
    public void Postgres(string scenario)
    {
        // The restricted role, so the grants are tested too.
        using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("FINMIND_API_TEST_DATABASE")!);
        Scenarios[scenario](new PostgresLedger(dataSource));
    }
}
