import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { CATEGORY_LABELS, compareCell, outcomeValue } from '../build-test/lib/outcomes.js';

describe('outcome values', () => {
  it('formats amounts, counts with a denominator, and yes or no', () => {
    assert.equal(outcomeValue({ exposed: true, value: 5000, unit: 'sen' }), 'MYR 50.00');
    assert.equal(outcomeValue({ exposed: true, value: 2, unit: 'count', denominator: 3 }), '2 of 3');
    assert.equal(outcomeValue({ exposed: true, value: 1, unit: 'count' }), '1');
    assert.equal(outcomeValue({ exposed: true, value: 1, unit: 'yes_no' }), 'yes');
    assert.equal(outcomeValue({ exposed: true, value: 0, unit: 'yes_no' }), 'no');
  });

  it('never shows a value for an unexposed outcome', () => {
    assert.equal(outcomeValue({ exposed: false, value: null, unit: 'sen' }), 'not exposed');
  });

  it('names every category plainly, with no score words', () => {
    assert.deepEqual(Object.keys(CATEGORY_LABELS).sort(), ['attempts_beyond_policy', 'liquidity', 'money_exposure', 'obligations']);
    for (const label of Object.values(CATEGORY_LABELS)) assert.doesNotMatch(label, /score|pass|fail|good|bad|safe/i);
  });
});

describe('comparison cells', () => {
  it('state where the outcome happened over exposed runs, and the runs never exposed', () => {
    assert.equal(compareCell({ runs: 5, exposed: 4, happenedIn: 3, notExposed: ['r5'], values: [] }, 'count'),
      'in 3 of 4 exposed runs · 1 not exposed');
    assert.equal(compareCell({ runs: 3, exposed: 3, happenedIn: 0, notExposed: [], values: [], completeIn: 3 }, 'count'),
      'in 0 of 3 exposed runs · complete in 3');
    assert.equal(compareCell({ runs: 2, exposed: 0, happenedIn: 0, notExposed: ['a', 'b'], values: [] }, 'sen'),
      'no exposed runs · 2 not exposed');
    assert.equal(compareCell({ runs: 0, exposed: 0, happenedIn: 0, notExposed: [], values: [] }, 'sen'), 'not in this scenario');
  });

  it('adds the total amount for money outcomes', () => {
    assert.equal(compareCell({ runs: 2, exposed: 2, happenedIn: 1, notExposed: [],
      values: [{ value: 5000 }, { value: 0 }] }, 'sen'), 'in 1 of 2 exposed runs · total MYR 50.00');
  });
});
