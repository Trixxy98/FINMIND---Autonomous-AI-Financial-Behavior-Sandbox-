import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { PALETTE, SPRITES, spriteRects } from '../build-test/lib/sprites.js';
import { SCENE, placePosition, sceneAt } from '../build-test/lib/scene.js';
import { eventsToCues } from '../build-test/lib/cues.js';
import { replayAt } from '../build-test/lib/replay.js';

describe('sprites', () => {
  it('are rectangular grids of known palette entries', () => {
    for (const [name, rows] of Object.entries(SPRITES)) {
      assert.ok(rows.length > 0, name);
      for (const row of rows) {
        assert.equal(row.length, rows[0].length, `${name} is not rectangular`);
        for (const ch of row) assert.ok(ch === '.' || ch in PALETTE, `${name} uses unknown colour ${ch}`);
      }
    }
  });
  it('render one rectangle per coloured pixel and none for transparent ones', () => {
    const rects = spriteRects(['a.', '.b'], 4);
    assert.deepEqual(rects, [{ x: 0, y: 0, size: 4, fill: PALETTE.a }, { x: 4, y: 4, size: 4, fill: PALETTE.b }]);
  });
  it('include every place the scene needs, and a neutral avatar', () => {
    for (const name of ['avatar', 'home', 'mailbox', 'counter', 'vault', 'postbox', 'desk', 'stall', 'door', 'coin', 'letter', 'sun']) {
      assert.ok(SPRITES[name], name);
    }
  });
});

describe('layout', () => {
  const merchants = Array.from({ length: 8 }, (_, i) => `merchant:${i}`);
  it('gives every place its own position inside the scene', () => {
    const places = ['home', 'mailbox', 'counter', 'vault', 'postbox', 'desk', 'missing_door', 'wallet', 'treasury', ...merchants];
    const seen = new Set();
    for (const place of places) {
      const { x, y } = placePosition(place, merchants);
      assert.ok(x >= 0 && x < SCENE.width && y >= 0 && y < SCENE.height, place);
      const key = `${x},${y}`;
      if (place !== 'wallet') { assert.ok(!seen.has(key), `${place} overlaps`); seen.add(key); }
    }
  });
});

const cash = 'c', savings = 's', shop = 'm', treasury = 't';
const events = [
  { sequence: 1, eventType: 'ACCOUNT_CREATED', payload: { accountId: treasury, kind: 'simulator_treasury' } },
  { sequence: 2, eventType: 'ACCOUNT_CREATED', payload: { accountId: cash, kind: 'agent_cash' } },
  { sequence: 3, eventType: 'LEDGER_COMMITTED', payload: { kind: 'opening_funding', postings: [{ accountId: treasury, amountSen: -1_000_000 }, { accountId: cash, amountSen: 1_000_000 }] } },
  { sequence: 4, eventType: 'ACCOUNT_CREATED', payload: { accountId: savings, kind: 'agent_savings' } },
  { sequence: 5, eventType: 'ACCOUNT_CREATED', payload: { accountId: shop, kind: 'merchant', label: 'Kedai (synthetic)' } },
  { sequence: 6, eventType: 'SCENARIO_MATERIALIZED', payload: {} },
  { sequence: 7, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0001', payload: { toolCallId: 'turn-0001', tool: 'move_to_savings' } },
  { sequence: 8, eventType: 'LEDGER_COMMITTED', traceId: 'turn-0001', payload: { kind: 'savings_move', postings: [{ accountId: cash, amountSen: -100_000 }, { accountId: savings, amountSen: 100_000 }] } },
  { sequence: 9, eventType: 'TOOL_RETURNED', traceId: 'turn-0001', payload: { toolCallId: 'turn-0001', tool: 'move_to_savings', status: 'committed' } },
  { sequence: 10, eventType: 'TOOL_CALL_PROPOSED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'edit_ledger' } },
  { sequence: 11, eventType: 'TOOL_RETURNED', traceId: 'turn-0002', payload: { toolCallId: 'turn-0002', tool: 'edit_ledger', status: 'unsupported_capability' } },
  { sequence: 12, eventType: 'AGENT_FINAL_REPORT', traceId: 'turn-0003', payload: { summary: 'All done.' } },
  { sequence: 13, eventType: 'RUN_STATUS_CHANGED', payload: { from: 'RUNNING', to: 'COMPLETED' } },
];
const cues = eventsToCues(events);

describe('scene state', () => {
  it('takes its figures from the replay at the same event', () => {
    for (const e of events) {
      const scene = sceneAt(events, cues, e.sequence);
      const replay = replayAt(events, e.sequence);
      assert.deepEqual([scene.cashSen, scene.savingsSen, scene.day, scene.status],
        [replay.cashSen, replay.savingsSen, replay.day, replay.status], `at ${e.sequence}`);
    }
  });
  it('places the avatar where the latest proposal went, and home when the run ends', () => {
    assert.equal(sceneAt(events, cues, 6).avatarAt, 'home');
    assert.equal(sceneAt(events, cues, 8).avatarAt, 'vault');
    assert.equal(sceneAt(events, cues, 11).avatarAt, 'missing_door');
    assert.equal(sceneAt(events, cues, 13).avatarAt, 'home');
  });
  it('shows the cue of the current event, and at most one at a time', () => {
    assert.equal(sceneAt(events, cues, 8).cue.kind, 'coins');
    assert.equal(sceneAt(events, cues, 11).cue.kind, 'missing_door');
    assert.equal(sceneAt(events, cues, 12).cue.kind, 'speech');
    assert.equal(sceneAt(events, cues, 9).cue, null); // a committed return adds nothing to show
  });
  it('lists merchants in the order they were created', () => {
    assert.deepEqual(sceneAt(events, cues, 13).merchants, [{ place: `merchant:${shop}`, label: 'Kedai (synthetic)' }]);
  });
});
