import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { batchRequest, comparisonRows, formatRate, formatSenMean, parseSeeds } from '../build-test/lib/analysis.js';
import { createApi } from '../build-test/lib/api.js';

describe('summary formatting', () => {
  it('rounds a mean in sen to the nearest sen with integer arithmetic, half to even', () => {
    assert.equal(formatSenMean('98500.33'), '≈ MYR 985.00');
    assert.equal(formatSenMean('98500.50'), '≈ MYR 985.00');
    assert.equal(formatSenMean('98501.50'), '≈ MYR 985.02');
    assert.equal(formatSenMean('-150.75'), '≈ -MYR 1.51');
    assert.equal(formatSenMean('300.00'), 'MYR 3.00');
    assert.equal(formatSenMean(null), '—');
  });
  it('shows rates as percentages with their counts', () => {
    assert.equal(formatRate({ numerator: 3, denominator: 12, applicable: true, value: 0.25 }), '25.0% (3 of 12)');
    assert.equal(formatRate({ numerator: 0, denominator: 0, applicable: false, value: null }), 'not applicable (0 of 0)');
  });
});

describe('batch requests', () => {
  it('parses an explicit seed list that matches the count', () => {
    assert.deepEqual(parseSeeds('7, 8,9', 3), { seeds: [7, 8, 9] });
    assert.deepEqual(parseSeeds('', 3), { seeds: undefined });
    assert.equal(parseSeeds('7, 8', 3).error, 'Give exactly 3 seeds, or none.');
    assert.equal(parseSeeds('7, x, 9', 3).error, 'Seeds are whole numbers from 0 to 2147483647.');
    assert.equal(parseSeeds('1.5, 2, 3', 3).error, 'Seeds are whole numbers from 0 to 2147483647.');
  });
  it('builds the request the API expects', () => {
    assert.deepEqual(batchRequest({ scenarioId: 'S01' }, '3', '', ' S01 baseline ', true),
      { body: { config: { scenarioId: 'S01' }, count: 3, label: 'S01 baseline', start: true } });
    assert.deepEqual(batchRequest({}, '2', '4,5', '', false), { body: { config: {}, count: 2, seeds: [4, 5], start: false } });
    assert.equal(batchRequest({}, '21', '', '', true).error, 'Count is 1 to 20.');
  });
});

describe('comparison table', () => {
  const comparison = {
    groups: [{ batchId: 'a', label: 'S01', n: 3 }, { batchId: 'b', label: 'S03', n: 2 }],
    metrics: [
      { name: 'finalNetWorthSen', groups: [
        { batchId: 'a', unit: 'sen', n: 3, mean: '98500.00', min: 98500, max: 98500, sd: 0, ci95: null, values: [] },
        { batchId: 'b', unit: 'sen', n: 2, mean: '1000000.50', min: 1000000, max: 1000001, sd: 0.71, ci95: null, values: [] }] },
      { name: 'unauthorizedAttemptRate', groups: [
        { batchId: 'a', unit: 'rate', n: 0, notApplicable: 3, mean: null, min: null, max: null, sd: null, ci95: null, values: [] },
        { batchId: 'b', unit: 'rate', n: 2, notApplicable: 0, mean: 0.25, min: 0.2, max: 0.3, sd: 0.0707, ci95: null, values: [] }] },
    ],
  };
  it('turns each metric into one row with a cell per group, never hiding n', () => {
    const rows = comparisonRows(comparison);
    assert.equal(rows[0].label, 'Final net worth');
    assert.deepEqual(rows[0].cells.map((c) => [c.n, c.mean, c.range]),
      [[3, 'MYR 985.00', 'MYR 985.00 – MYR 985.00'], [2, '≈ MYR 10,000.00', 'MYR 10,000.00 – MYR 10,000.01']]);
    assert.equal(rows[0].cells[1].interval, 'no interval below n = 5');
    assert.deepEqual(rows[1].cells.map((c) => [c.n, c.mean]), [[0, 'none applicable (3 runs)'], [2, '25.0%']]);
  });
});

describe('export download', () => {
  it('fetches with the Authorization header and never puts the token in the URL', async () => {
    const calls = [];
    const api = createApi({
      token: () => 'tok-export',
      fetchImpl: async (url, init) => { calls.push({ url, init }); return new Response('{"type":"header"}\n', { status: 200 }); },
    });
    const blob = await api.exportRun('r1', 'jsonl');
    assert.equal(await blob.text(), '{"type":"header"}\n');
    assert.equal(calls[0].url, '/api/v1/runs/r1/export?format=jsonl');
    assert.equal(calls[0].init.headers.Authorization, 'Bearer tok-export');
    assert.ok(!calls[0].url.includes('tok-export'));
  });
});
