// ADR-0015 step 5: formatting for metrics and comparison. Money stays integer: a mean in sen arrives as an
// exact decimal string, and is rounded to the sen here with integer arithmetic.
import { formatSen } from './money.js';
import { formatMicroUsd } from './runForm.js';

// "98501.50" -> 98502n (half to even), and whether rounding changed it.
function roundToWhole(text: string): { value: bigint; exact: boolean } | null {
  const match = /^(-?)(\d+)\.(\d{2})$/.exec(text);
  if (!match) return null;
  let whole = BigInt(match[2]);
  const fraction = Number(match[3]);
  if (fraction > 50 || (fraction === 50 && whole % 2n === 1n)) whole += 1n;
  return { value: match[1] === '-' ? -whole : whole, exact: fraction === 0 };
}

export function formatSenMean(text: string | null | undefined): string {
  if (text == null) return '—';
  const rounded = roundToWhole(text);
  if (!rounded) return text;
  return `${rounded.exact ? '' : '≈ '}${formatSen(Number(rounded.value))}`;
}

function formatMicroMean(text: string | null): string {
  if (text == null) return '—';
  const rounded = roundToWhole(text);
  return rounded ? `${rounded.exact ? '' : '≈ '}${formatMicroUsd(Number(rounded.value))}` : text;
}

const percent = (value: number) => `${(value * 100).toFixed(1)}%`;

export function formatRate(rate: { numerator: number; denominator: number; applicable: boolean; value: number | null }): string {
  return rate.applicable && rate.value != null
    ? `${percent(rate.value)} (${rate.numerator} of ${rate.denominator})`
    : `not applicable (${rate.numerator} of ${rate.denominator})`;
}

export const METRIC_LABELS: Record<string, string> = {
  finalNetWorthSen: 'Final net worth',
  netWorthChangeSen: 'Net worth change',
  fundsReceivedSen: 'Funds received',
  fundsSpentSen: 'Funds spent (net of reversals)',
  unauthorizedAttemptRate: 'Unauthorized attempt rate',
  policyComplianceRate: 'Policy compliance rate',
  fundingRequests: 'Funding requests',
  fundingAmountAskedSen: 'Funding amount asked',
  insufficientFunds: 'Insufficient-funds mistakes',
  malformedAttempts: 'Malformed attempts',
  integrityViolations: 'Integrity violations (platform defects)',
  actions: 'Actions',
  modelTokens: 'Model tokens',
  costMicroUsd: 'Model cost',
};

export interface Cell { batchId: string; n: number; mean: string; range: string; sd: string; interval: string; values: any[] }
export interface Row { name: string; label: string; cells: Cell[] }

// Each metric is one row, with one cell per group. n is always shown.
export function comparisonRows(comparison: any): Row[] {
  return comparison.metrics.map((metric: any) => ({
    name: metric.name,
    label: METRIC_LABELS[metric.name] ?? metric.name,
    cells: metric.groups.map((g: any): Cell => {
      const money = (v: number) => formatSen(Math.round(v));
      const micro = (v: number) => formatMicroUsd(Math.round(v));
      let mean: string, range: string, sd: string, low = '', high = '';
      if (g.unit === 'rate') {
        mean = g.mean == null ? (g.notApplicable ? `none applicable (${g.notApplicable} runs)` : '—') : percent(g.mean);
        range = g.min == null ? '—' : `${percent(g.min)} – ${percent(g.max)}`;
        sd = g.sd == null ? '—' : `${(g.sd * 100).toFixed(1)} points`;
        if (g.ci95) [low, high] = [percent(g.ci95.low), percent(g.ci95.high)];
      } else if (g.unit === 'sen') {
        mean = formatSenMean(g.mean);
        range = g.min == null ? '—' : `${formatSen(g.min)} – ${formatSen(g.max)}`;
        sd = g.sd == null ? '—' : `≈ ${money(g.sd)}`;
        if (g.ci95) [low, high] = [money(g.ci95.low), money(g.ci95.high)];
      } else if (g.unit === 'microusd') {
        mean = formatMicroMean(g.mean);
        range = g.min == null ? '—' : `${formatMicroUsd(g.min)} – ${formatMicroUsd(g.max)}`;
        sd = g.sd == null ? '—' : `≈ ${micro(g.sd)}`;
        if (g.ci95) [low, high] = [micro(g.ci95.low), micro(g.ci95.high)];
      } else {
        mean = g.mean ?? '—';
        range = g.min == null ? '—' : `${g.min} – ${g.max}`;
        sd = g.sd == null ? '—' : g.sd.toFixed(2);
        if (g.ci95) [low, high] = [g.ci95.low.toFixed(2), g.ci95.high.toFixed(2)];
      }
      const interval = g.ci95 ? `${low} – ${high}` : g.n < 5 ? 'no interval below n = 5' : '—';
      return { batchId: g.batchId, n: g.n, mean, range, sd, interval, values: g.values };
    }),
  }));
}

export function parseSeeds(text: string, count: number): { seeds?: number[]; error?: string } {
  if (!text.trim()) return { seeds: undefined };
  const parts = text.split(/[\s,]+/).filter(Boolean);
  if (parts.some((p) => !/^\d{1,10}$/.test(p) || Number(p) > 2_147_483_647)) {
    return { error: 'Seeds are whole numbers from 0 to 2147483647.' };
  }
  if (parts.length !== count) return { error: `Give exactly ${count} seeds, or none.` };
  return { seeds: parts.map(Number) };
}

export function batchRequest(config: unknown, countText: string, seedsText: string, label: string, start: boolean):
  { body?: Record<string, unknown>; error?: string } {
  const count = /^\d{1,2}$/.test(countText.trim()) ? Number(countText.trim()) : 0;
  if (count < 1 || count > 20) return { error: 'Count is 1 to 20.' };
  const parsed = parseSeeds(seedsText, count);
  if (parsed.error) return { error: parsed.error };
  const body: Record<string, unknown> = { config, count, start };
  if (parsed.seeds) body.seeds = parsed.seeds;
  if (label.trim()) body.label = label.trim();
  return { body };
}
