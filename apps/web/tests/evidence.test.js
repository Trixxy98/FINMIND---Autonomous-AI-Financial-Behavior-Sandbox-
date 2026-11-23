import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { evidenceLabel } from '../build-test/lib/evidence.js';

// ADR-0014: the label is the API's evidence value; the browser never derives one.
describe('evidenceLabel', () => {
  it('names each API value', () => {
    assert.equal(evidenceLabel('proposed').text, 'PROPOSED');
    assert.equal(evidenceLabel('denied').text, 'DENIED');
    assert.equal(evidenceLabel('committed').text, 'COMMITTED');
    assert.equal(evidenceLabel('reported_by_agent').text, 'REPORTED BY AGENT');
    assert.equal(evidenceLabel('verified_by_ledger').text, 'VERIFIED BY LEDGER');
  });
  it('gives each label its own style', () => {
    const kinds = ['proposed', 'denied', 'committed', 'reported_by_agent', 'verified_by_ledger'].map((v) => evidenceLabel(v).tone);
    assert.equal(new Set(kinds).size, 5);
  });
  it('never guesses an unknown value', () => {
    assert.equal(evidenceLabel('verified').text, 'UNKNOWN EVIDENCE');
    assert.equal(evidenceLabel(undefined).text, 'UNKNOWN EVIDENCE');
  });
});
