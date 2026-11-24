import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { budgetLines, isTerminal, modelTextFor, refreshesOn, turnOf } from '../build-test/lib/runView.js';

describe('run view helpers', () => {
  it('maps a tool call to its model turn', () => {
    assert.equal(turnOf('turn-0003'), 'turn-0003');
    assert.equal(turnOf('turn-0003-2'), 'turn-0003');
    assert.equal(turnOf('unreadable-abc'), null);
    assert.equal(turnOf('call-7'), null);
  });

  it('finds the model text of a turn, never from another event type', () => {
    const events = [
      { eventType: 'MODEL_COST_RESERVED', traceId: 'turn-0003', payload: {} },
      { eventType: 'MODEL_INVOCATION_COMPLETED', traceId: 'turn-0003', payload: { outputText: 'I will pay rent.', model: 'm' } },
      { eventType: 'MODEL_INVOCATION_COMPLETED', traceId: 'turn-0004', payload: { outputText: 'other' } },
    ];
    assert.equal(modelTextFor(events, 'turn-0003-1').outputText, 'I will pay rent.');
    assert.equal(modelTextFor(events, 'turn-0009'), null);
  });

  it('knows terminal statuses', () => {
    for (const s of ['COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED']) assert.ok(isTerminal(s));
    for (const s of ['DRAFT', 'QUEUED', 'INITIALIZING', 'RUNNING', null]) assert.ok(!isTerminal(s));
  });

  it('refreshes derived views only on events that change them', () => {
    for (const t of ['TOOL_RETURNED', 'LEDGER_COMMITTED', 'RUN_STATUS_CHANGED', 'CLOCK_ADVANCED', 'MODEL_INVOCATION_COMPLETED',
      'JOURNAL_REVERSED', 'TOOL_CALL_PROPOSED']) assert.ok(refreshesOn(t), t);
    for (const t of ['MODEL_COST_RESERVED', 'SCENARIO_MATERIALIZED']) assert.ok(!refreshesOn(t), t);
  });

  it('formats budgets with exact units and denominators', () => {
    const lines = budgetLines({
      actions: { used: 12, max: 100 },
      modelTokens: { used: 0, max: 0 },
      costMicroUsd: { recorded: 120_000, reserved: 330_000, max: 500_000 },
      wallTimeSeconds: { elapsed: 61, max: 900 },
    });
    assert.deepEqual(lines, [
      'Actions: 12 of 100',
      'Model tokens: 0 (no model budget)',
      'Cost: US$0.120000 recorded + US$0.330000 reserved of US$0.500000 (estimate)',
      'Wall time: 61 s of 900 s',
    ]);
    assert.deepEqual(budgetLines(null), ['No budgets: development run.']);
  });
});
