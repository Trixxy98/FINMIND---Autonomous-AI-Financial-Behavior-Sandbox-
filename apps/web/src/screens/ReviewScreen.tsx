import { useCallback, useEffect, useState } from 'react';
import type { Api } from '../lib/api.js';
import { ClassificationsPanel } from '../components/ClassificationsPanel.js';

// ADR-0015: finished research runs with candidates that still need a person's decision.
export function ReviewScreen({ api, onOpenRun }: { api: Api; onOpenRun: (runId: string) => void }) {
  const [queue, setQueue] = useState<any>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const load = useCallback(async () => setQueue(await api.pendingReviews()), [api]);
  useEffect(() => { void load(); }, [load]);
  if (!queue) return <section><h2>Review</h2><p>Loading…</p></section>;
  return (
    <section>
      <h2>Review</h2>
      <p style={{ fontSize: 12, color: '#5f6368' }}>{queue.note} <button type="button" onClick={() => void load()}>Refresh</button></p>
      <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
        <ul style={{ flex: '0 0 300px', listStyle: 'none', padding: 0 }}>
          {queue.runs.map((r: any) => (
            <li key={r.runId} style={{ border: '1px solid #ddd', borderRadius: 6, padding: 6, marginBottom: 6,
              background: r.runId === selected ? '#e8f0fe' : undefined }}>
              <button type="button" onClick={() => setSelected(r.runId)}>{r.unresolved} of {r.candidates} open</button>{' '}
              {r.scenarioId} · {r.agent?.modelId}
              <div style={{ fontSize: 11 }}>{r.runId} <button type="button" onClick={() => onOpenRun(r.runId)}>run</button></div>
            </li>
          ))}
          {queue.runs.length === 0 && <li>Nothing waits for review.</li>}
        </ul>
        <div style={{ flex: '1 1 auto' }}>
          {selected ? <ClassificationsPanel key={selected} api={api} runId={selected} openOnly onDecided={() => void load()} />
            : <p>Choose a run.</p>}
        </div>
      </div>
    </section>
  );
}
