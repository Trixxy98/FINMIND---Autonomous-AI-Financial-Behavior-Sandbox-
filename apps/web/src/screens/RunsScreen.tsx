import { useCallback, useEffect, useState } from 'react';
import type { Api } from '../lib/api.js';
import { EvidenceBadge } from '../components/EvidenceBadge.js';

const STATUSES = ['', 'DRAFT', 'QUEUED', 'INITIALIZING', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED'];
const PAGE = 20;
const LABELS = ['committed', 'denied', 'reported_by_agent', 'proposed'];

interface Props {
  api: Api;
  onOpen: (runId: string) => void;
  onNew: () => void;
  onCopy: (snapshot: any) => void;
}

export function RunsScreen({ api, onOpen, onNew, onCopy }: Props) {
  const [status, setStatus] = useState('');
  const [kind, setKind] = useState<'' | 'managed' | 'legacy'>('');
  const [runs, setRuns] = useState<any[]>([]);
  const [details, setDetails] = useState<Record<string, any>>({});
  const [nextBefore, setNextBefore] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const load = useCallback(async (before?: string) => {
    setLoading(true);
    setError('');
    try {
      const page = await api.listRuns({ status: status || undefined, kind: kind || undefined, limit: PAGE, before });
      setRuns((current) => (before ? [...current, ...page.runs] : page.runs));
      setNextBefore(page.runs.length === PAGE ? page.nextBefore : null);
      // Counts by evidence come from each run's detail; the list does not carry them.
      const results = await Promise.allSettled(page.runs.map((run: any) => api.getRun(run.runId)));
      setDetails((current) => {
        const next = before ? { ...current } : {};
        results.forEach((result, index) => {
          if (result.status === 'fulfilled') next[page.runs[index].runId] = result.value;
        });
        return next;
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load runs.');
    } finally {
      setLoading(false);
    }
  }, [api, status, kind]);

  useEffect(() => { void load(); }, [load]);

  return (
    <section>
      <h2>Runs</h2>
      <p>
        <button type="button" onClick={onNew}>New run</button>{' '}
        <label>Status{' '}
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            {STATUSES.map((s) => <option key={s} value={s}>{s || 'Any'}</option>)}
          </select>
        </label>{' '}
        <label>Kind{' '}
          <select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
            <option value="">Any</option>
            <option value="managed">Research runs</option>
            <option value="legacy">Development runs</option>
          </select>
        </label>{' '}
        <button type="button" onClick={() => void load()} disabled={loading}>Refresh</button>
      </p>
      {error && <p role="alert" style={{ color: '#a50e0e' }}>{error}</p>}
      <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 14 }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Created</th><th>Status</th><th>Scenario</th><th>Model</th><th>Research</th><th>Actions by evidence</th><th />
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => {
            const detail = details[run.runId];
            const counts = detail?.actionCounts;
            return (
              <tr key={run.runId} style={{ borderBottom: '1px solid #eee', verticalAlign: 'top' }}>
                <td>{new Date(run.createdAt).toLocaleString()}</td>
                <td>
                  {run.status ?? 'no lifecycle'}
                  {run.statusReason && <div style={{ fontSize: 12, color: '#5f6368' }}>{run.statusReason}</div>}
                </td>
                <td>{run.scenarioId ?? '—'}</td>
                <td>{run.agent ? `${run.agent.provider} / ${run.agent.modelId}` : '—'}{run.agent && run.agent.provider !== 'mock' && <strong> (paid)</strong>}</td>
                <td>{run.researchEligible ? 'Eligible' : 'Development run (excluded from results)'}</td>
                <td>
                  {counts ? (
                    <div>
                      <div>{counts.total} actions</div>
                      {LABELS.map((label) => (
                        <div key={label}><EvidenceBadge value={label} /> {counts[label]} of {counts.total}</div>
                      ))}
                    </div>
                  ) : '…'}
                </td>
                <td>
                  <button type="button" onClick={() => onOpen(run.runId)}>Open</button>{' '}
                  {detail?.configSnapshot && <button type="button" onClick={() => onCopy(detail.configSnapshot)}>Copy configuration</button>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {!loading && runs.length === 0 && <p>No runs match.</p>}
      {nextBefore && <p><button type="button" onClick={() => void load(nextBefore)} disabled={loading}>Load more</button></p>}
    </section>
  );
}
