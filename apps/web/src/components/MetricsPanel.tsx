import { formatSen } from '../lib/money.js';
import { formatRate } from '../lib/analysis.js';
import { EvidenceBadge } from './EvidenceBadge.js';

// ADR-0015: finmind-metrics/v1 for one run. Every rate shows its numerator and denominator.
export function MetricsPanel({ metrics }: { metrics: any }) {
  const m = metrics.metrics;
  const row = (label: string, value: React.ReactNode, note?: string) => (
    <tr><th style={{ textAlign: 'left', fontWeight: 500, paddingRight: 16, verticalAlign: 'top' }}>{label}</th>
      <td>{value}{note && <div style={{ fontSize: 12, color: '#5f6368' }}>{note}</div>}</td></tr>
  );
  const integrity = m.integrityViolations;
  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>{metrics.metricsVersion} · as of event {metrics.asOfSequence}
        {!metrics.researchEligible && <strong> · development run, excluded from results</strong>}</p>
      <table style={{ fontSize: 14 }}><tbody>
        {row('Final net worth', <>{formatSen(m.finalNetWorthSen.value)} <EvidenceBadge value="verified_by_ledger" /></>, m.finalNetWorthSen.definition)}
        {row('Net worth change', formatSen(m.netWorthChangeSen.value), `from a starting balance of ${formatSen(m.netWorthChangeSen.startingBalanceSen)}`)}
        {row('Funds received', formatSen(m.fundsReceivedSen.value), `${m.fundsReceivedSen.definition}; ${formatSen(m.fundsReceivedSen.fromReversalsSen)} came back through reversals`)}
        {row('Funds spent', formatSen(m.fundsSpentSen.value), `${formatSen(m.fundsSpentSen.grossSen)} gross, ${formatSen(m.fundsSpentSen.reversedSen)} reversed`)}
        {row('Unauthorized attempt rate', formatRate(m.unauthorizedAttemptRate), m.unauthorizedAttemptRate.definition)}
        {row('Policy compliance rate', formatRate(m.policyComplianceRate), m.policyComplianceRate.definition)}
        {row('Mistakes', `${m.mistakes.insufficientFunds} insufficient funds, ${m.mistakes.malformed} malformed, ${m.mistakes.otherDenials} other denials, ${m.mistakes.noOutcome} without outcome`,
          `of ${m.mistakes.outOf} financial attempts`)}
        {row('Funding requests', <>{m.fundingRequests.count} asking {formatSen(m.fundingRequests.amountAskedSen)} <EvidenceBadge value="reported_by_agent" /></>,
          'What the agent asked for. Nothing is ever credited.')}
        {row('Integrity violations', <strong style={{ color: integrity.value ? '#a50e0e' : undefined }}>{integrity.value}{integrity.kinds.length ? ` (${integrity.kinds.join(', ')})` : ''}</strong>,
          'Platform defects, not agent behavior. Expected 0.')}
        {row('Budgets', `${m.budgets.actions.used} actions, ${m.budgets.modelTokens.used} tokens, ${m.budgets.modelLatency.turns} model turns`)}
      </tbody></table>
    </div>
  );
}
