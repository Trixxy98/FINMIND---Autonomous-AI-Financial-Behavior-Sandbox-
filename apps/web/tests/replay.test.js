import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { replayAt } from '../build-test/lib/replay.js';

const cash = 'c', savings = 's', shop = 'm', treasury = 't';
const events = Object.freeze([
  { sequence: 1, eventType: 'RUN_CREATED', payload: {} },
  { sequence: 2, eventType: 'RUN_STATUS_CHANGED', payload: { from: 'DRAFT', to: 'QUEUED' } },
  { sequence: 3, eventType: 'ACCOUNT_CREATED', payload: { accountId: treasury, kind: 'simulator_treasury' } },
  { sequence: 4, eventType: 'ACCOUNT_CREATED', payload: { accountId: cash, kind: 'agent_cash' } },
  { sequence: 5, eventType: 'LEDGER_COMMITTED', journalId: 'j1', payload: { kind: 'opening_funding', postings: [{ accountId: treasury, amountSen: -1_000_000 }, { accountId: cash, amountSen: 1_000_000 }] } },
  { sequence: 6, eventType: 'ACCOUNT_CREATED', payload: { accountId: savings, kind: 'agent_savings' } },
  { sequence: 7, eventType: 'ACCOUNT_CREATED', payload: { accountId: shop, kind: 'merchant', label: 'Shop (synthetic)' } },
  { sequence: 8, eventType: 'SCENARIO_MATERIALIZED', payload: {} },
  { sequence: 9, eventType: 'RUN_STATUS_CHANGED', payload: { from: 'INITIALIZING', to: 'RUNNING' } },
  { sequence: 10, eventType: 'CLOCK_ADVANCED', traceId: 'turn-0001', payload: { fromDay: 0, toDay: 3 } },
  { sequence: 11, eventType: 'OBLIGATION_CREATED', payload: { obligationId: 'o1', amountSen: 150_000, label: 'Rent', dueDay: 5 } },
  { sequence: 12, eventType: 'SCENARIO_EVENT_DELIVERED', payload: { eventKey: 'notice-1', kind: 'message', deliveredAtDay: 3 } },
  { sequence: 13, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'pay_bill' } },
  { sequence: 14, eventType: 'LEDGER_COMMITTED', traceId: 'turn-0002', journalId: 'j2', payload: { kind: 'bill_payment', postings: [{ accountId: cash, amountSen: -150_000 }, { accountId: shop, amountSen: 150_000 }] } },
  { sequence: 15, eventType: 'OBLIGATION_PAID', payload: { obligationId: 'o1', journalId: 'j2' } },
  { sequence: 16, eventType: 'TOOL_RETURNED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'pay_bill', status: 'committed' } },
  { sequence: 17, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0003', payload: { toolCallId: 'turn-0003', tool: 'create_money' } },
  { sequence: 18, eventType: 'TOOL_RETURNED', traceId: 'turn-0003', payload: { toolCallId: 'turn-0003', tool: 'create_money', status: 'unsupported_capability', reasonCode: 'UNSUPPORTED_CAPABILITY' } },
  { sequence: 19, eventType: 'LEDGER_COMMITTED', journalId: 'j3', payload: { kind: 'reversal', postings: [{ accountId: shop, amountSen: -150_000 }, { accountId: cash, amountSen: 150_000 }] } },
  { sequence: 20, eventType: 'OBLIGATION_PAYMENT_REVERSED', payload: { obligationId: 'o1' } },
  { sequence: 21, eventType: 'SOMETHING_NEW', payload: {} },
  { sequence: 22, eventType: 'RUN_STATUS_CHANGED', payload: { from: 'RUNNING', to: 'COMPLETED', reason: 'agent finished' } },
]);

describe('replay', () => {
  it('starts empty before the first event', () => {
    const s = replayAt(events, 0);
    assert.deepEqual([s.sequence, s.status, s.day, s.cashSen, s.savingsSen], [0, null, null, 0, 0]);
  });

  it('rebuilds balances, day, bills, and the action in progress at any event', () => {
    const s = replayAt(events, 13);
    assert.deepEqual([s.status, s.day, s.cashSen, s.savingsSen], ['RUNNING', 3, 1_000_000, 0]);
    assert.deepEqual(s.bills, [{ obligationId: 'o1', label: 'Rent', amountSen: 150_000, dueDay: 5, paid: false }]);
    assert.deepEqual(s.messages, [{ key: 'notice-1', day: 3 }]);
    assert.deepEqual(s.currentAction, { toolCallId: 'turn-0002', tool: 'pay_bill', outcome: null, reasonCode: null });
    assert.equal(replayAt(events, 16).bills[0].paid, true);
    assert.equal(replayAt(events, 16).cashSen, 850_000);
  });

  it('a reversed payment makes the bill unpaid again, as in the ledger', () => {
    const s = replayAt(events, 20);
    assert.equal(s.cashSen, 1_000_000);
    assert.equal(s.bills[0].paid, false);
  });

  it('counts outcomes so far and ignores event types it does not know', () => {
    const s = replayAt(events, 22);
    assert.deepEqual(s.outcomes, { committed: 1, unsupported_capability: 1 });
    assert.equal(s.status, 'COMPLETED');
    assert.equal(s.lastEvent.eventType, 'RUN_STATUS_CHANGED');
  });

  it('is a pure function of the events and the position', () => {
    assert.deepEqual(replayAt(events, 16), replayAt(events, 16));
    assert.deepEqual(replayAt(events, 999), replayAt(events, 22));
  });
});
