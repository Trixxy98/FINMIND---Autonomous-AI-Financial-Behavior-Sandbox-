// ADR-0015 step 5: read-only replay. The state at an event is rebuilt from the recorded events up to it.
// It writes nothing and calls no model; it shows what was recorded, in order.
export interface Bill { obligationId: string; label: string | null; amountSen: number; dueDay: number | null; paid: boolean }
export interface ReplayState {
  sequence: number;
  status: string | null;
  day: number | null;
  cashSen: number;
  savingsSen: number;
  bills: Bill[];
  messages: { key: string; day: number }[];
  currentAction: { toolCallId: string; tool: string; outcome: string | null; reasonCode: string | null } | null;
  outcomes: Record<string, number>;
  lastEvent: any | null;
}

export function replayAt(events: readonly any[], sequence: number): ReplayState {
  const kinds = new Map<string, string>();
  const balances = new Map<string, number>();
  const bills = new Map<string, Bill>();
  const state: ReplayState = {
    sequence: 0, status: null, day: null, cashSen: 0, savingsSen: 0, bills: [], messages: [], currentAction: null,
    outcomes: {}, lastEvent: null,
  };

  for (const event of events) {
    if (event.sequence > sequence) break;
    const p = event.payload ?? {};
    state.sequence = event.sequence;
    state.lastEvent = event;
    switch (event.eventType) {
      case 'ACCOUNT_CREATED': kinds.set(p.accountId, p.kind); break;
      case 'LEDGER_COMMITTED':
        for (const posting of p.postings ?? []) {
          balances.set(posting.accountId, (balances.get(posting.accountId) ?? 0) + posting.amountSen);
        }
        break;
      case 'RUN_STATUS_CHANGED': state.status = p.to ?? state.status; break;
      case 'SCENARIO_MATERIALIZED': state.day = 0; break;
      case 'CLOCK_ADVANCED': state.day = p.toDay; break;
      case 'OBLIGATION_CREATED':
        bills.set(p.obligationId, { obligationId: p.obligationId, label: p.label ?? null, amountSen: p.amountSen, dueDay: p.dueDay ?? null, paid: false });
        break;
      case 'OBLIGATION_PAID': { const bill = bills.get(p.obligationId); if (bill) bills.set(p.obligationId, { ...bill, paid: true }); break; }
      case 'OBLIGATION_PAYMENT_REVERSED': { const bill = bills.get(p.obligationId); if (bill) bills.set(p.obligationId, { ...bill, paid: false }); break; }
      case 'SCENARIO_EVENT_DELIVERED':
        if (p.kind === 'message') state.messages = [...state.messages, { key: p.eventKey, day: p.deliveredAtDay }];
        break;
      case 'TOOL_CALL_PROPOSED':
        state.currentAction = { toolCallId: p.toolCallId, tool: p.tool, outcome: null, reasonCode: null };
        break;
      case 'TOOL_RETURNED':
        state.outcomes = { ...state.outcomes, [p.status]: (state.outcomes[p.status] ?? 0) + 1 };
        if (state.currentAction && state.currentAction.toolCallId === p.toolCallId) {
          state.currentAction = { ...state.currentAction, outcome: p.status, reasonCode: p.reasonCode ?? null };
        }
        break;
      default: break; // event types replay does not show
    }
  }

  for (const [account, balance] of balances) {
    if (kinds.get(account) === 'agent_cash') state.cashSen += balance;
    if (kinds.get(account) === 'agent_savings') state.savingsSen += balance;
  }
  state.bills = [...bills.values()];
  return state;
}
