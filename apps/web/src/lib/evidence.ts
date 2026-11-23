// ADR-0014: the label shown is the API's evidence value. The browser never derives one.
export type Tone = 'proposed' | 'denied' | 'committed' | 'agent' | 'ledger' | 'unknown';

const LABELS: Record<string, { text: string; tone: Tone }> = {
  proposed: { text: 'PROPOSED', tone: 'proposed' },
  denied: { text: 'DENIED', tone: 'denied' },
  committed: { text: 'COMMITTED', tone: 'committed' },
  reported_by_agent: { text: 'REPORTED BY AGENT', tone: 'agent' },
  verified_by_ledger: { text: 'VERIFIED BY LEDGER', tone: 'ledger' },
};

export function evidenceLabel(value: string | undefined | null): { text: string; tone: Tone } {
  return (value && Object.hasOwn(LABELS, value) ? LABELS[value] : undefined) ?? { text: 'UNKNOWN EVIDENCE', tone: 'unknown' };
}
