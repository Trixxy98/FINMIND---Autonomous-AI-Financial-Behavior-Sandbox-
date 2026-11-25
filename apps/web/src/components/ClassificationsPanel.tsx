import { useCallback, useEffect, useState } from 'react';
import type { Api } from '../lib/api.js';
import { formatSen } from '../lib/money.js';
import { EvidenceBadge } from './EvidenceBadge.js';

const LABELS = ['NORMAL_ACTION', 'REQUEST_FOR_CLARIFICATION', 'POLICY_DENIAL', 'UNSUPPORTED_CAPABILITY_ATTEMPT',
  'UNAUTHORIZED_FUNDING_REQUEST', 'BALANCE_MISREPORT', 'DUPLICATE_PAYMENT_ATTEMPT', 'POTENTIAL_PROMPT_INJECTION_COMPLIANCE',
  'RECONCILIATION_ATTEMPT', 'MODEL_ERROR', 'UNKNOWN_REVIEW'];

function ReviewForm({ item, onSave }: { item: any; onSave: (label: string, note: string) => Promise<void> }) {
  const [label, setLabel] = useState<string>(item.suggestedLabel ?? item.label);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  return (
    <div style={{ marginTop: 6 }}>
      <label>Decision{' '}
        <select value={label} onChange={(e) => setLabel(e.target.value)}>{LABELS.map((l) => <option key={l}>{l}</option>)}</select>
      </label>{' '}
      <label>Note <input style={{ width: 320 }} maxLength={2000} value={note} onChange={(e) => setNote(e.target.value)}
        placeholder="What the evidence shows" /></label>{' '}
      <button type="button" disabled={busy} onClick={async () => {
        setBusy(true); setError('');
        try { await onSave(label, note.trim()); setNote(''); } catch (e) { setError(e instanceof Error ? e.message : 'Not saved.'); }
        finally { setBusy(false); }
      }}>Record decision</button>
      {error && <span role="alert" style={{ color: '#a50e0e' }}> {error}</span>}
    </div>
  );
}

// ADR-0015: rule labels and review candidates. A decision is appended; earlier ones stay visible.
export function ClassificationsPanel({ api, runId, openOnly = false, onDecided }:
  { api: Api; runId: string; openOnly?: boolean; onDecided?: () => void }) {
  const [data, setData] = useState<any>(null);
  const [showAll, setShowAll] = useState(!openOnly);
  const load = useCallback(async () => setData(await api.classifications(runId)), [api, runId]);
  useEffect(() => { void load(); }, [load]);
  if (!data) return <p>Loading classifications…</p>;

  const items = data.items.filter((i: any) => showAll || i.status === 'needs_review' || i.kind === 'candidate');
  const nonZero = Object.entries(data.counts.byLabel).filter(([, n]) => (n as number) > 0);
  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>{data.classificationVersion}. Rules label what is mechanical; anything that needs
        reading the agent's text is a candidate until a person decides. No model judges.</p>
      <p><strong>{data.counts.unresolved}</strong> of {data.counts.candidates} candidates need a decision · {data.counts.items} items over{' '}
        {data.counts.actions} actions · {nonZero.map(([l, n]) => `${l} ${n}`).join(', ')}{' '}
        <label><input type="checkbox" checked={showAll} onChange={(e) => setShowAll(e.target.checked)} /> show rule labels too</label></p>
      {items.map((item: any) => (
        <div key={item.key} style={{ border: '1px solid #ddd', borderRadius: 6, padding: 8, marginBottom: 8,
          background: item.status === 'needs_review' ? '#fffdf5' : undefined }}>
          <div><strong>{item.label}</strong>{item.suggestedLabel && item.status === 'needs_review' && <> · suggested {item.suggestedLabel}</>}
            {' '}· {item.ruleId} · events {item.sequences.join(', ')} · <em>{item.status.replace('_', ' ')}</em></div>
          <div style={{ fontSize: 12, color: '#5f6368' }}>{item.rule}{item.tool && ` · ${item.tool} (${item.toolCallId})`}</div>
          {item.excerpt && (
            <blockquote style={{ margin: '6px 0', padding: '4px 8px', borderLeft: '3px solid #f9ab00', background: '#fef7e0', whiteSpace: 'pre-wrap' }}>
              <EvidenceBadge value="reported_by_agent" /> {item.excerpt}
            </blockquote>
          )}
          {item.ledgerContext && (
            <div>Ledger at event {item.ledgerContext.asOfSequence}: cash {formatSen(item.ledgerContext.cashSen)}, savings{' '}
              {formatSen(item.ledgerContext.savingsSen)} <EvidenceBadge value="verified_by_ledger" /></div>
          )}
          {item.reviews.length > 0 && (
            <ul style={{ fontSize: 12, margin: '4px 0' }}>
              {item.reviews.map((r: any) => <li key={r.reviewId}>{new Date(r.createdAt).toLocaleString()} · {r.reviewer}: {r.label}{r.note ? ` — ${r.note}` : ''}</li>)}
            </ul>
          )}
          <ReviewForm item={item} onSave={async (label, note) => {
            await api.review(runId, item.key, label, note || undefined);
            await load();
            onDecided?.(); // so a queue shows the new count
          }} />
        </div>
      ))}
      {items.length === 0 && <p>Nothing needs a decision.</p>}
    </div>
  );
}
