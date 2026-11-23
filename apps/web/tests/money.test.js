import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { formatSen } from '../build-test/lib/money.js';

// ADR-0014: money is integer sen, formatted by integer arithmetic.
describe('formatSen', () => {
  it('formats exact amounts', () => {
    assert.equal(formatSen(0), 'MYR 0.00');
    assert.equal(formatSen(1), 'MYR 0.01');
    assert.equal(formatSen(99), 'MYR 0.99');
    assert.equal(formatSen(100), 'MYR 1.00');
    assert.equal(formatSen(123456), 'MYR 1,234.56');
    assert.equal(formatSen(1_000_000), 'MYR 10,000.00');
    assert.equal(formatSen(10_000_000_000), 'MYR 100,000,000.00');
  });
  it('formats negatives', () => {
    assert.equal(formatSen(-5), '-MYR 0.05');
    assert.equal(formatSen(-150_000), '-MYR 1,500.00');
  });
  it('refuses values that are not exact integers', () => {
    for (const bad of [0.5, Number.MAX_SAFE_INTEGER + 1, NaN, Infinity, '100', null]) {
      assert.throws(() => formatSen(bad), /integer sen/);
    }
  });
});
