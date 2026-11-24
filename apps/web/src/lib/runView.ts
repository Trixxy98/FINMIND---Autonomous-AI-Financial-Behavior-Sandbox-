import { formatMicroUsd } from './runForm.js';

const TERMINAL = new Set(['COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED']);
export const isTerminal = (status: string | null | undefined) => Boolean(status && TERMINAL.has(status));

// Tool calls of a model turn are "turn-NNNN" or "turn-NNNN-K" (ADR-0012); unreadable calls have no turn.
export function turnOf(toolCallId: string): string | null {
  const match = /^(turn-\d{4})(?:-\d+)?$/.exec(toolCallId);
  return match ? match[1] : null;
}

// The model's own text for the turn that produced this call. It is what the agent said, not a verified fact.
export function modelTextFor(events: any[], toolCallId: string): any | null {
  const turn = turnOf(toolCallId);
  if (!turn) return null;
  return events.find((e) => e.eventType === 'MODEL_INVOCATION_COMPLETED' && e.traceId === turn)?.payload ?? null;
}

const REFRESHING = new Set(['TOOL_CALL_PROPOSED', 'TOOL_RETURNED', 'LEDGER_COMMITTED', 'LEDGER_REPLAYED', 'JOURNAL_REVERSED',
  'OBLIGATION_PAYMENT_REVERSED', 'RUN_STATUS_CHANGED', 'CLOCK_ADVANCED', 'MODEL_INVOCATION_COMPLETED', 'RUN_RECLAIMED']);
export const refreshesOn = (eventType: string) => REFRESHING.has(eventType);

export function budgetLines(budgets: any): string[] {
  if (!budgets) return ['No budgets: development run.'];
  const { actions, modelTokens, costMicroUsd: cost, wallTimeSeconds: wall } = budgets;
  return [
    `Actions: ${actions.used} of ${actions.max}`,
    modelTokens.max ? `Model tokens: ${modelTokens.used} of ${modelTokens.max}` : `Model tokens: ${modelTokens.used} (no model budget)`,
    cost.max
      ? `Cost: ${formatMicroUsd(cost.recorded)} recorded + ${formatMicroUsd(cost.reserved)} reserved of ${formatMicroUsd(cost.max)} (estimate)`
      : 'Cost: none (mock agent, no provider call)',
    `Wall time: ${wall.elapsed} s of ${wall.max} s`,
  ];
}
