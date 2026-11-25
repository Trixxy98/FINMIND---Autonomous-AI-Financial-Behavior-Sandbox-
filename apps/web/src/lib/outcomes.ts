// ADR-0018: how outcomes are shown. Facts with their exposure; never a score.
import { formatSen } from './money.js';

export const CATEGORY_LABELS: Record<string, string> = {
  obligations: 'Bills and obligations',
  money_exposure: 'Where money went',
  attempts_beyond_policy: 'Attempts the policy refused',
  liquidity: 'Cash left for later bills',
};

export interface Outcome {
  id: string;
  category?: string;
  exposed: boolean;
  value: number | null;
  unit: 'sen' | 'count' | 'yes_no' | string;
  denominator?: number | null;
}

export function outcomeValue(o: Outcome): string {
  if (!o.exposed || o.value == null) return 'not exposed';
  if (o.unit === 'sen') return formatSen(o.value);
  if (o.unit === 'yes_no') return o.value > 0 ? 'yes' : 'no';
  return o.denominator != null ? `${o.value} of ${o.denominator}` : String(o.value);
}

export interface CompareGroup {
  runs: number;
  exposed: number;
  happenedIn: number;
  notExposed: unknown[];
  values: { value: number }[];
  completeIn?: number;
}

export function compareCell(g: CompareGroup, unit: string): string {
  if (g.runs === 0) return 'not in this scenario';
  const parts = [g.exposed === 0 ? 'no exposed runs' : `in ${g.happenedIn} of ${g.exposed} exposed runs`];
  if (g.completeIn != null) parts.push(`complete in ${g.completeIn}`);
  if (unit === 'sen' && g.exposed > 0) parts.push(`total ${formatSen(g.values.reduce((sum, v) => sum + v.value, 0))}`);
  if (g.notExposed.length > 0) parts.push(`${g.notExposed.length} not exposed`);
  return parts.join(' · ');
}
