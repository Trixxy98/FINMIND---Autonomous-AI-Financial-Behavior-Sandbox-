import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildConfig, defaultForm, effortAfterModelChange, effortsFor, formFromSnapshot, formatMicroUsd, modelLabel, modelNotices, parseMyrToSen, parseUsdToMicro, validateForm }
  from '../build-test/lib/runForm.js';

describe('amount parsing is exact and integer-only', () => {
  it('MYR to sen', () => {
    assert.equal(parseMyrToSen('10000'), 1_000_000);
    assert.equal(parseMyrToSen('10,000.5'), 1_000_050);
    assert.equal(parseMyrToSen('0.01'), 1);
    assert.equal(parseMyrToSen('100000000.00'), 10_000_000_000);
    for (const bad of ['', '-1', '1.234', '1e5', 'abc', '100000000.01', '0', '0.00', ' 5 x']) {
      assert.equal(parseMyrToSen(bad), null, bad);
    }
  });
  it('USD to micro-dollars', () => {
    assert.equal(parseUsdToMicro('0.5'), 500_000);
    assert.equal(parseUsdToMicro('1.000001'), 1_000_001);
    assert.equal(parseUsdToMicro('100'), 100_000_000);
    for (const bad of ['', '-1', '0.0000001', '100.01', 'x', '0']) assert.equal(parseUsdToMicro(bad), null, bad);
    assert.equal(formatMicroUsd(500_000), 'US$0.500000');
    assert.equal(formatMicroUsd(1_234_567), 'US$1.234567');
  });
});

describe('configuration', () => {
  const scenario = { id: 'S02', versionLabel: 'S02/v1', recommendedObjective: 'Pay the bills.' };

  it('builds a mock configuration without provider settings or a cost budget', () => {
    const form = defaultForm(scenario, { provider: 'mock', modelId: 'finmind-mock-v1' });
    const config = buildConfig(form);
    assert.deepEqual(config, {
      objective: 'Pay the bills.', scenarioId: 'S02', seed: 42, startingBalanceSen: 1_000_000,
      transactionLimitSen: 400_000, durationDays: 30, maxActions: 100, maxModelTokens: 0, maxWallTimeSeconds: 900,
      memoryMode: 'none', describeUnavailableCapabilities: false,
      agent: { provider: 'mock', modelId: 'finmind-mock-v1' },
    });
    assert.deepEqual(validateForm(form), []);
  });

  it('a paid model needs effort, a token budget, and a cost budget', () => {
    const form = defaultForm(scenario, { provider: 'anthropic', modelId: 'claude-opus-5-5' });
    assert.deepEqual(validateForm({ ...form, maxModelTokens: '0', maxCostUsd: '' }).sort(), ['maxCostMicroUsd', 'maxModelTokens']);
    const config = buildConfig({ ...form, maxCostUsd: '0.50', effort: 'low' });
    assert.deepEqual(config.agent, { provider: 'anthropic', modelId: 'claude-opus-5-5', settings: { effort: 'low' } });
    assert.equal(config.maxCostMicroUsd, 500_000);
  });

  it('reports bad numbers by field name', () => {
    const form = defaultForm(scenario, { provider: 'mock', modelId: 'finmind-mock-v1' });
    const errors = validateForm({ ...form, startingBalance: '1.234', seed: '-1', durationDays: '366' });
    assert.deepEqual(errors.sort(), ['durationDays', 'seed', 'startingBalanceSen']);
    // As on the server: the limit is compared with the starting balance only when that balance is valid.
    assert.deepEqual(validateForm({ ...form, transactionLimit: '20000' }), ['transactionLimitSen']);
  });

  it('copies a run snapshot, keeping only the researcher fields, and flags a scenario version change', () => {
    const snapshot = {
      objective: 'Old objective', scenarioId: 'S02', seed: 7, startingBalanceSen: 2_000_050, transactionLimitSen: 50_000,
      durationDays: 10, maxActions: 30, maxModelTokens: 300_000, maxWallTimeSeconds: 600, maxCostMicroUsd: 1_500_000,
      memoryMode: 'none', describeUnavailableCapabilities: true,
      agent: { provider: 'anthropic', modelId: 'claude-sonnet-5-5', settings: { effort: 'high', maxOutputTokensPerTurn: 8000 } },
      configSchemaVersion: 'run-config/v2', scenarioVersion: 'S02/v0', promptHash: 'abc', codeRevision: 'r1',
    };
    const { form, warnings } = formFromSnapshot(snapshot, [scenario]);
    const config = buildConfig(form);
    assert.equal(form.startingBalance, '20000.50');
    assert.equal(form.maxCostUsd, '1.500000');
    assert.deepEqual(config.agent.settings, { effort: 'high', maxOutputTokensPerTurn: 8000 });
    for (const key of ['configSchemaVersion', 'scenarioVersion', 'promptHash', 'codeRevision']) assert.ok(!(key in config));
    assert.deepEqual(warnings, ['The copied run used S02/v0; a new run uses S02/v1.']);
  });
});

describe('providers in the form (ADR-0019 step 5)', () => {
  const openai = { provider: 'openai', providerLabel: 'OpenAI (GPT)', modelId: 'gpt-6.2-sol', paid: true, verifiedLive: false,
    efforts: ['low', 'medium', 'high'], notice: null };
  const google = { provider: 'google', providerLabel: 'Google (Gemini)', modelId: 'gemini-4-hydrogen', paid: true, verifiedLive: false,
    efforts: ['low', 'medium', 'high'], notice: 'On the free tier, Google uses prompts to improve its products.' };
  const claude = { provider: 'anthropic', providerLabel: 'Anthropic (Claude)', modelId: 'claude-opus-5-5', paid: true, verifiedLive: true,
    efforts: ['low', 'medium', 'high', 'xhigh', 'max'], notice: null };

  it('offers only the efforts the model accepts, and validates against them', () => {
    assert.deepEqual(effortsFor(openai), ['low', 'medium', 'high']);
    assert.deepEqual(effortsFor({ provider: 'anthropic', modelId: 'x', paid: true }), ['low', 'medium', 'high', 'xhigh', 'max']);
    const form = { ...defaultForm({ id: 'S01', latest: true }, openai), maxCostUsd: '0.50' };
    assert.deepEqual(validateForm({ ...form, effort: 'xhigh' }, openai), ['agent.settings.effort']);
    assert.deepEqual(validateForm({ ...form, effort: 'high' }, openai), []);
    assert.deepEqual(validateForm({ ...form, effort: 'xhigh' }, claude), []);
  });

  it('keeps a chosen effort when the new model accepts it, and falls back to medium otherwise', () => {
    assert.equal(effortAfterModelChange('high', openai), 'high');
    assert.equal(effortAfterModelChange('max', openai), 'medium');
  });

  it('warns when a provider is not verified live, and shows its own notice', () => {
    const notices = modelNotices(google);
    assert.equal(notices.length, 2);
    assert.match(notices[0], /not yet verified against Google \(Gemini\)'s live API/);
    assert.match(notices[0], /smoke_provider\.py --provider google/);
    assert.equal(notices[1], google.notice);
    assert.deepEqual(modelNotices(claude), []);
    assert.deepEqual(modelNotices({ provider: 'mock', modelId: 'finmind-mock-v1', paid: false, verifiedLive: true }), []);
  });

  it('labels a model with its provider and whether it is verified', () => {
    assert.equal(modelLabel(openai), 'OpenAI (GPT) / gpt-6.2-sol (paid, not verified live)');
    assert.equal(modelLabel(claude), 'Anthropic (Claude) / claude-opus-5-5 (paid)');
    assert.equal(modelLabel({ provider: 'mock', modelId: 'finmind-mock-v1', paid: false }), 'mock / finmind-mock-v1');
  });
});
