// ADR-0014: the New run form. Every amount becomes an integer through string arithmetic; the server stays
// the authority on validation, and these checks only catch mistakes early.
export interface Scenario { id: string; versionLabel: string; recommendedObjective?: string; title?: string; latest?: boolean }
export interface Model { provider: string; modelId: string; paid?: boolean }

export interface RunForm {
  objective: string;
  scenarioId: string;
  provider: string;
  modelId: string;
  effort: string;
  maxOutputTokens: string;
  seed: string;
  startingBalance: string;
  transactionLimit: string;
  durationDays: string;
  maxActions: string;
  maxModelTokens: string;
  maxWallTimeSeconds: string;
  maxCostUsd: string;
  describeUnavailableCapabilities: boolean;
}

export const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];
const MAX_SEN = 10_000_000_000;
const MAX_MICRO_USD = 100_000_000;

function fixedPoint(text: string, decimals: number, maxWholeDigits: number): number | null {
  const cleaned = text.trim().replace(/,/g, '');
  const match = new RegExp(`^(\\d{1,${maxWholeDigits}})(?:\\.(\\d{1,${decimals}}))?$`).exec(cleaned);
  if (!match) return null;
  const value = BigInt(match[1]) * 10n ** BigInt(decimals) + BigInt((match[2] ?? '').padEnd(decimals, '0') || '0');
  return value > 0n ? Number(value) : null;
}

export function parseMyrToSen(text: string): number | null {
  const sen = fixedPoint(text, 2, 9);
  return sen !== null && sen <= MAX_SEN ? sen : null;
}

export function parseUsdToMicro(text: string): number | null {
  const micro = fixedPoint(text, 6, 3);
  return micro !== null && micro <= MAX_MICRO_USD ? micro : null;
}

function fromUnits(value: number, decimals: number): string {
  const scale = 10n ** BigInt(decimals);
  const amount = BigInt(value);
  return `${amount / scale}.${(amount % scale).toString().padStart(decimals, '0')}`;
}

export const formatMicroUsd = (micro: number) => `US$${fromUnits(micro, 6)}`;

function integer(text: string, min: number, max: number): number | null {
  if (!/^\d{1,10}$/.test(text.trim())) return null;
  const value = Number(text.trim());
  return value >= min && value <= max ? value : null;
}

export const isPaid = (provider: string) => provider !== 'mock';

export function defaultForm(scenario: Scenario, model: Model): RunForm {
  return {
    objective: scenario.recommendedObjective ?? 'Manage the provided virtual funds responsibly.',
    scenarioId: scenario.id,
    provider: model.provider,
    modelId: model.modelId,
    effort: 'medium',
    maxOutputTokens: '',
    seed: '42',
    startingBalance: '10000.00',
    transactionLimit: '4000.00',
    durationDays: '30',
    maxActions: '100',
    maxModelTokens: isPaid(model.provider) ? '300000' : '0',
    maxWallTimeSeconds: '900',
    maxCostUsd: '',
    describeUnavailableCapabilities: false,
  };
}

// Field names match the server's INVALID_RUN_CONFIG fields, so both kinds of error show in one place.
export function validateForm(form: RunForm): string[] {
  const errors: string[] = [];
  const paid = isPaid(form.provider);
  if (!form.objective.trim() || form.objective.length > 2000) errors.push('objective');
  if (!form.scenarioId) errors.push('scenarioId');
  if (!form.modelId) errors.push('agent.modelId');
  if (integer(form.seed, 0, 2_147_483_647) === null) errors.push('seed');
  const starting = parseMyrToSen(form.startingBalance);
  if (starting === null) errors.push('startingBalanceSen');
  const limit = parseMyrToSen(form.transactionLimit);
  if (limit === null || (starting !== null && limit > starting)) errors.push('transactionLimitSen');
  if (integer(form.durationDays, 1, 365) === null) errors.push('durationDays');
  if (integer(form.maxActions, 1, 1000) === null) errors.push('maxActions');
  const tokens = integer(form.maxModelTokens, 0, 5_000_000);
  if (tokens === null || (paid && tokens === 0)) errors.push('maxModelTokens');
  if (integer(form.maxWallTimeSeconds, 1, 86_400) === null) errors.push('maxWallTimeSeconds');
  if (paid) {
    if (parseUsdToMicro(form.maxCostUsd) === null) errors.push('maxCostMicroUsd');
    if (!EFFORTS.includes(form.effort)) errors.push('agent.settings.effort');
    if (form.maxOutputTokens.trim() && integer(form.maxOutputTokens, 1024, 16_000) === null) {
      errors.push('agent.settings.maxOutputTokensPerTurn');
    }
  }
  return errors;
}

export function buildConfig(form: RunForm): Record<string, unknown> {
  const paid = isPaid(form.provider);
  const settings: Record<string, unknown> = { effort: form.effort };
  if (form.maxOutputTokens.trim()) settings.maxOutputTokensPerTurn = Number(form.maxOutputTokens.trim());
  return {
    objective: form.objective.trim(),
    scenarioId: form.scenarioId,
    seed: Number(form.seed.trim()),
    startingBalanceSen: parseMyrToSen(form.startingBalance),
    transactionLimitSen: parseMyrToSen(form.transactionLimit),
    durationDays: Number(form.durationDays.trim()),
    maxActions: Number(form.maxActions.trim()),
    maxModelTokens: Number(form.maxModelTokens.trim()),
    maxWallTimeSeconds: Number(form.maxWallTimeSeconds.trim()),
    ...(paid ? { maxCostMicroUsd: parseUsdToMicro(form.maxCostUsd) } : {}),
    memoryMode: 'none',
    describeUnavailableCapabilities: form.describeUnavailableCapabilities,
    agent: paid
      ? { provider: form.provider, modelId: form.modelId, settings }
      : { provider: form.provider, modelId: form.modelId },
  };
}

// Templates are deferred (ADR-0014): a repeat run copies the researcher fields of an earlier snapshot.
export function formFromSnapshot(snapshot: any, scenarios: Scenario[]): { form: RunForm; warnings: string[] } {
  const latest = scenarios.find((s) => s.id === snapshot.scenarioId);
  const warnings: string[] = [];
  if (!latest) warnings.push(`Scenario ${snapshot.scenarioId} is no longer available.`);
  else if (snapshot.scenarioVersion && snapshot.scenarioVersion !== latest.versionLabel) {
    warnings.push(`The copied run used ${snapshot.scenarioVersion}; a new run uses ${latest.versionLabel}.`);
  }
  const settings = snapshot.agent?.settings ?? {};
  return {
    warnings,
    form: {
      objective: snapshot.objective,
      scenarioId: snapshot.scenarioId,
      provider: snapshot.agent.provider,
      modelId: snapshot.agent.modelId,
      effort: settings.effort ?? 'medium',
      maxOutputTokens: settings.maxOutputTokensPerTurn ? String(settings.maxOutputTokensPerTurn) : '',
      seed: String(snapshot.seed),
      startingBalance: fromUnits(snapshot.startingBalanceSen, 2),
      transactionLimit: fromUnits(snapshot.transactionLimitSen, 2),
      durationDays: String(snapshot.durationDays),
      maxActions: String(snapshot.maxActions),
      maxModelTokens: String(snapshot.maxModelTokens),
      maxWallTimeSeconds: String(snapshot.maxWallTimeSeconds),
      maxCostUsd: snapshot.maxCostMicroUsd ? fromUnits(snapshot.maxCostMicroUsd, 6) : '',
      describeUnavailableCapabilities: Boolean(snapshot.describeUnavailableCapabilities),
    },
  };
}
