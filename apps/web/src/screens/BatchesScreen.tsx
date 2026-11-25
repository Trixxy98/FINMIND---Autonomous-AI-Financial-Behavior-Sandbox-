import { useCallback, useEffect, useState } from 'react';
import type { Api } from '../lib/api.js';

interface Props { api: Api; onNewBatch: () => void; onOpenRun: (runId: string) => void; onCompare: (batchIds: string[]) => void }

export function BatchesScreen({ api, onNewBatch, onOpenRun, onCompare }: Props) {
  const [batches, setBatches] = useState<any[]>([]);
  const [open, setOpen] = useState<any>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    try { setBatches((await api.listBatches()).batches); setError(''); }
    catch (e) { setError(e instanceof Error ? e.message : 'Could not load batches.'); }
  }, [api]);
  useEffect(() => { void load(); }, [load]);

  const toggle = (id: string) => setChosen((c) => (c.includes(id) ? c.filter((x) => x !== id) : [...c, id]));

  return (
    <section>
      <h2>Batches</h2>
      <p>
        <button type="button" onClick={onNewBatch}>New batch</button>{' '}
        <button type="button" onClick={() => void load()}>Refresh</button>{' '}
        <button type="button" disabled={chosen.length < 2 || chosen.length > 6} onClick={() => onCompare(chosen)}>
          Compare selected ({chosen.length})
        </button>{' '}
        <span style={{ fontSize: 12, color: '#5f6368' }}>Choose 2 to 6 batches.</span>
      </p>
      {error && <p role="alert" style={{ color: '#a50e0e' }}>{error}</p>}
      <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 14 }}>
        <thead><tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
          <th>Compare</th><th>Created</th><th>Label</th><th>Scenario</th><th>Model</th><th>Runs</th><th>Seeds</th><th /></tr></thead>
        <tbody>
          {batches.map((b) => (
            <tr key={b.batchId} style={{ borderBottom: '1px solid #eee' }}>
              <td><input type="checkbox" aria-label={`Compare ${b.label ?? b.batchId}`} checked={chosen.includes(b.batchId)} onChange={() => toggle(b.batchId)} /></td>
              <td>{new Date(b.createdAt).toLocaleString()}</td><td>{b.label ?? '—'}</td><td>{b.scenarioId}</td>
              <td>{b.agent.provider} / {b.agent.modelId}{b.agent.provider !== 'mock' && <strong> (paid)</strong>}</td>
              <td>{b.count}</td><td>{b.seedMode === 'same' ? 'same seed' : 'seed list'}</td>
              <td><button type="button" onClick={async () => setOpen(await api.getBatch(b.batchId))}>Details</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {batches.length === 0 && !error && <p>No batches yet.</p>}
      {open && (
        <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 12, marginTop: 12 }}>
          <h3 style={{ marginTop: 0 }}>{open.label ?? open.batchId}</h3>
          <p>{open.count} runs, {open.seedMode === 'same' ? 'same configuration and seed' : `seeds ${open.seeds.join(', ')}`}.
            Started in the order of positions {open.startOrder.join(', ')} (order seed {open.orderSeed}).
            {!open.complete && <strong> Not every run was created.</strong>}</p>
          <table style={{ fontSize: 13 }}><tbody>
            {open.runs.map((r: any) => (
              <tr key={r.runId}><td>#{r.batchPosition}</td><td>{r.status}{r.statusReason ? ` — ${r.statusReason}` : ''}</td>
                <td><button type="button" onClick={() => onOpenRun(r.runId)}>Open run</button></td></tr>
            ))}
          </tbody></table>
          <button type="button" onClick={() => setOpen(null)}>Close</button>
        </div>
      )}
    </section>
  );
}
