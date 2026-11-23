import { evidenceLabel } from '../lib/evidence.js';

// ADR-0014: a badge always shows the label as text; color only reinforces it.
const TONE_STYLE: Record<string, { background: string; color: string }> = {
  proposed: { background: '#e8eaed', color: '#202124' },
  denied: { background: '#fce8e6', color: '#a50e0e' },
  committed: { background: '#e6f4ea', color: '#0d652d' },
  agent: { background: '#fef7e0', color: '#7a4f01' },
  ledger: { background: '#e8f0fe', color: '#174ea6' },
  unknown: { background: '#ffffff', color: '#a50e0e' },
};

export function EvidenceBadge({ value }: { value: string | undefined }) {
  const label = evidenceLabel(value);
  return (
    <span style={{ ...TONE_STYLE[label.tone], padding: '1px 6px', borderRadius: 4, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap' }}>
      {label.text}
    </span>
  );
}
