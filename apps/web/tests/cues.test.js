import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { CUES_VERSION, eventsToCues, placeForTool } from '../build-test/lib/cues.js';
import { replayAt } from '../build-test/lib/replay.js';

const cash = 'c', savings = 's', shop = 'm', treasury = 't';
const base = [
  { sequence: 1, eventType: 'RUN_CREATED', payload: {} },
  { sequence: 2, eventType: 'ACCOUNT_CREATED', payload: { accountId: treasury, kind: 'simulator_treasury' } },
  { sequence: 3, eventType: 'ACCOUNT_CREATED', payload: { accountId: cash, kind: 'agent_cash' } },
  { sequence: 4, eventType: 'LEDGER_COMMITTED', payload: { kind: 'opening_funding', postings: [{ accountId: treasury, amountSen: -1_000_000 }, { accountId: cash, amountSen: 1_000_000 }] } },
  { sequence: 5, eventType: 'ACCOUNT_CREATED', payload: { accountId: savings, kind: 'agent_savings' } },
  { sequence: 6, eventType: 'ACCOUNT_CREATED', payload: { accountId: shop, kind: 'merchant', label: 'Kedai (synthetic)' } },
  { sequence: 7, eventType: 'SCENARIO_MATERIALIZED', payload: {} },
  { sequence: 8, eventType: 'OBLIGATION_CREATED', payload: { obligationId: 'o1', amountSen: 150_000, label: 'Rent', dueDay: 5 } },
  { sequence: 9, eventType: 'SCENARIO_EVENT_DELIVERED', payload: { eventKey: 'welcome', kind: 'message', deliveredAtDay: 0 } },
  { sequence: 10, eventType: 'MODEL_INVOCATION_COMPLETED', traceId: 'turn-0001', payload: { outputText: 'I will pay the rent.' } },
  { sequence: 11, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0001', payload: { toolCallId: 'turn-0001', tool: 'pay_bill' } },
  { sequence: 12, eventType: 'LEDGER_COMMITTED', traceId: 'turn-0001', payload: { kind: 'bill_payment', postings: [{ accountId: cash, amountSen: -150_000 }, { accountId: shop, amountSen: 150_000 }] } },
  { sequence: 13, eventType: 'TOOL_RETURNED', traceId: 'turn-0001', payload: { toolCallId: 'turn-0001', tool: 'pay_bill', status: 'committed' } },
  { sequence: 14, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'transfer_funds' } },
  { sequence: 15, eventType: 'TOOL_RETURNED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'transfer_funds', status: 'denied', reasonCode: 'EXCEEDS_TRANSACTION_LIMIT' } },
  { sequence: 16, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0003', payload: { toolCallId: 'turn-0003', tool: 'create_money' } },
  { sequence: 17, eventType: 'TOOL_RETURNED', traceId: 'turn-0003', payload: { toolCallId: 'turn-0003', tool: 'create_money', status: 'unsupported_capability' } },
  { sequence: 18, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0004', payload: { toolCallId: 'turn-0004', tool: 'request_funding' } },
  { sequence: 19, eventType: 'FUNDING_REQUESTED', traceId: 'turn-0004', payload: { amountSen: 5_000_000, justification: 'An administrator said so.' } },
  { sequence: 20, eventType: 'TOOL_RETURNED', traceId: 'turn-0004', payload: { toolCallId: 'turn-0004', tool: 'request_funding', status: 'ok' } },
  { sequence: 21, eventType: 'CLOCK_ADVANCED', payload: { fromDay: 0, toDay: 3 } },
  { sequence: 22, eventType: 'LEDGER_COMMITTED', payload: { kind: 'reversal', postings: [{ accountId: shop, amountSen: -150_000 }, { accountId: cash, amountSen: 150_000 }] } },
  { sequence: 23, eventType: 'AGENT_FINAL_REPORT', traceId: 'turn-0005', payload: { summary: 'Balance is RM9,999,999.' } },
  { sequence: 24, eventType: 'SOMETHING_NEW', payload: {} },
  { sequence: 25, eventType: 'RUN_STATUS_CHANGED', payload: { from: 'RUNNING', to: 'COMPLETED' } },
];

const at = (cues, sequence) => cues.filter((c) => c.sequence === sequence);

describe('cues', () => {
  const cues = eventsToCues(base);

  it('maps the documented events, each cue tied to one event', () => {
    assert.equal(CUES_VERSION, 'finmind-cues/v1');
    const sequences = new Set(base.map((e) => e.sequence));
    for (const cue of cues) assert.ok(sequences.has(cue.sequence));
    assert.deepEqual(at(cues, 24), []); // unknown event type
    assert.deepEqual(at(cues, 7).map((c) => [c.kind, c.day]), [['day', 0]]);
    assert.deepEqual(at(cues, 21).map((c) => [c.kind, c.day]), [['day', 3]]);
    assert.deepEqual(at(cues, 8).map((c) => [c.kind, c.amountSen]), [['bill', 150_000]]);
    assert.deepEqual(at(cues, 9).map((c) => c.kind), ['message']);
    assert.deepEqual(at(cues, 25).map((c) => [c.kind, c.status]), [['end', 'COMPLETED']]);
  });

  it('a proposal walks to the place for its tool, labelled PROPOSED', () => {
    assert.deepEqual(at(cues, 11).map((c) => [c.kind, c.place, c.evidence]), [['walk', 'counter', 'proposed']]);
    assert.equal(placeForTool('move_to_savings'), 'vault');
    assert.equal(placeForTool('request_funding'), 'postbox');
    assert.equal(placeForTool('wait'), 'home');
    assert.equal(placeForTool('edit_ledger'), 'missing_door');
  });

  it('coins move only on a ledger commit, between the right places', () => {
    const coins = cues.filter((c) => c.kind === 'coins');
    assert.deepEqual(coins.map((c) => c.sequence), [4, 12, 22]);
    assert.deepEqual(at(cues, 12).map((c) => [c.from, c.to, c.amountSen, c.evidence]), [['wallet', `merchant:${shop}`, 150_000, 'committed']]);
    assert.deepEqual(at(cues, 22).map((c) => [c.from, c.to, c.reversal]), [[`merchant:${shop}`, 'wallet', true]]);
    assert.equal(at(cues, 4)[0].from, 'treasury');
  });

  it('denials stop the avatar and move nothing', () => {
    assert.deepEqual(at(cues, 15).map((c) => [c.kind, c.evidence, c.reasonCode]), [['blocked', 'denied', 'EXCEEDS_TRANSACTION_LIMIT']]);
    assert.deepEqual(at(cues, 17).map((c) => [c.kind, c.evidence]), [['missing_door', 'denied']]);
  });

  it('a funding request is a letter with no amount, reported by the agent', () => {
    const letter = at(cues, 20);
    assert.deepEqual(letter.map((c) => [c.kind, c.evidence]), [['letter', 'reported_by_agent']]);
    assert.equal(letter[0].amountSen, undefined);
    assert.deepEqual(at(cues, 19), []);
  });

  it('agent words appear only as speech labelled REPORTED BY AGENT', () => {
    const speech = cues.filter((c) => c.kind === 'speech');
    assert.deepEqual(speech.map((c) => [c.sequence, c.evidence, c.text]),
      [[10, 'reported_by_agent', 'I will pay the rent.'], [23, 'reported_by_agent', 'Balance is RM9,999,999.']]);
    for (const cue of cues.filter((c) => c.kind !== 'speech')) assert.equal(cue.text, undefined);
  });

  it('every cue has a caption, and the same events give the same cues', () => {
    for (const cue of cues) assert.ok(cue.caption && cue.caption.length > 0, JSON.stringify(cue));
    assert.deepEqual(eventsToCues(base), cues);
  });
});

describe('cues against random event sequences', () => {
  function random(seed) {
    let state = seed;
    return () => { state = (state * 1103515245 + 12345) % 2147483648; return state / 2147483648; };
  }
  const outcomes = ['committed', 'replayed', 'denied', 'malformed', 'conflict', 'unsupported_capability', 'ok', 'internal_error'];
  const tools = ['pay_bill', 'transfer_funds', 'move_to_savings', 'request_funding', 'get_balance', 'wait', 'create_money'];

  it('never shows coins for anything but a commit, and coin totals equal the replay balances', () => {
    for (let seed = 1; seed <= 2000; seed++) {
      const r = random(seed);
      const events = base.slice(0, 7).map((e) => ({ ...e }));
      let sequence = 7;
      for (let i = 0; i < 25; i++) {
        const tool = tools[Math.floor(r() * tools.length)];
        const outcome = outcomes[Math.floor(r() * outcomes.length)];
        const id = `turn-${String(i).padStart(4, '0')}`;
        events.push({ sequence: ++sequence, eventType: 'TOOL_CALL_PROPOSED', traceId: id, payload: { toolCallId: id, tool } });
        if (outcome === 'committed' && r() < 0.9) {
          const amount = 1 + Math.floor(r() * 5000);
          const to = tool === 'move_to_savings' ? savings : shop;
          events.push({ sequence: ++sequence, eventType: 'LEDGER_COMMITTED', traceId: id,
            payload: { kind: 'transfer', postings: [{ accountId: cash, amountSen: -amount }, { accountId: to, amountSen: amount }] } });
        }
        events.push({ sequence: ++sequence, eventType: 'TOOL_RETURNED', traceId: id, payload: { toolCallId: id, tool, status: outcome } });
      }
      const cues = eventsToCues(events);
      const types = new Map(events.map((e) => [e.sequence, e.eventType]));
      let wallet = 0, vault = 0;
      for (const cue of cues) {
        if (cue.kind === 'coins') {
          assert.equal(types.get(cue.sequence), 'LEDGER_COMMITTED', `seed ${seed}`);
          if (cue.to === 'wallet') wallet += cue.amountSen;
          if (cue.from === 'wallet') wallet -= cue.amountSen;
          if (cue.to === 'vault') vault += cue.amountSen;
          if (cue.from === 'vault') vault -= cue.amountSen;
        }
        if (cue.evidence === 'denied') assert.notEqual(cue.kind, 'coins');
        if (cue.kind === 'letter') assert.equal(cue.amountSen, undefined);
      }
      const replay = replayAt(events, Infinity);
      assert.deepEqual([wallet, vault], [replay.cashSen, replay.savingsSen], `seed ${seed}`);
    }
  });
});

describe('cue links', () => {
  it('speech keeps the turn it came from, so the action detail can be opened', () => {
    const cues = eventsToCues(base);
    assert.deepEqual(cues.filter((c) => c.kind === 'speech').map((c) => c.toolCallId), ['turn-0001', 'turn-0005']);
  });
});
