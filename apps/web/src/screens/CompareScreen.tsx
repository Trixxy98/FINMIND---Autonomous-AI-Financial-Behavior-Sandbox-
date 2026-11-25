import { useEffect, useState } from 'react';
import type { Api } from '../lib/api.js';
import { comparisonRows } from '../lib/analysis.js';

interface Props { api: Api; batchIds: string[]; onBack: () => void; onOpenRun: (runId: string) => void }

// ADR-0015: descriptive comparison. The varied factor is named first, so any other difference is visible.
export function CompareScreen({ api, batchIds, onBack, onOpenRun }: Props) {
  const [comparison, setComparison] = useState<any>(null);
  const [error, setError] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try { setComparison(await api.compare(batchIds)); }
      catch (e) { setError(e instanceof Error ? e.message : 'Could not compare.'); }
    })();
  }, [api, batchIds]);

  if (!comparison) return <section><button type="button" onClick={onBack}>Back</button><p>{error || 'Comparing…'}</p></section>;
  const config = comparison.configuration;
  const name = (batchId: string) => comparison.groups.find((g: any) => g.batchId === batchId)?.label ?? batchId.slice(0, 8);
  const rows = comparisonRows(comparison);

  return (
    <section>
      <p><button type="button" onClick={onBack}>Back to batches</button></p>
      <h2>Comparison</h2>
      <p style={{ fontSize: 12, color: '#5f6368' }}>{comparison.comparisonVersion} · {comparison.metricsVersion}. {comparison.note}</p>

      <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 12, marginBottom: 12 }}>
        <h3 style={{ marginTop: 0 }}>What differs</h3>
        <p><strong>Between groups:</strong> {config.betweenGroups.length ? config.betweenGroups.join(', ') : 'nothing: the groups have the same configuration'}</p>
        {config.withinGroups.map((fields: string[], i: number) => fields.length > 0 && (
          <p key={i}><strong>Within {name(comparison.groups[i].batchId)}:</strong> {fields.join(', ')}</p>
        ))}
        {config.metadata.length > 0 && (
          <p role="alert" style={{ background: '#fef7e0', padding: 8 }}>
            Reproducibility metadata also differs: {config.metadata.join(', ')}. Results may not be comparable.
          </p>
        )}
        {config.betweenGroups.length > 1 && (
          <p role="alert" style={{ background: '#fef7e0', padding: 8 }}>More than one field differs between groups, so a difference in results cannot be attributed to one of them.</p>
        )}
      </div>

      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginBottom: 12 }}>
        {comparison.groups.map((g: any) => (
          <div key={g.batchId} style={{ border: '1px solid #ddd', borderRadius: 6, padding: 8, flex: '1 1 240px' }}>
            <strong>{name(g.batchId)}</strong>: n = {g.n}
            {g.excluded.length > 0 && <ul style={{ margin: '4px 0', paddingLeft: 18, fontSize: 12 }}>
              {g.excluded.map((e: any) => <li key={e.runId}>excluded ({e.reason}){e.statusReason ? `: ${e.statusReason}` : ''}{' '}
                <button type="button" onClick={() => onOpenRun(e.runId)}>open</button></li>)}
            </ul>}
          </div>
        ))}
      </div>

      <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 13 }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Metric</th>{comparison.groups.map((g: any) => <th key={g.batchId}>{name(g.batchId)}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.name} style={{ borderBottom: '1px solid #eee', verticalAlign: 'top' }}>
              <td><button type="button" onClick={() => setExpanded(expanded === row.name ? null : row.name)}>{row.label}</button></td>
              {row.cells.map((cell) => (
                <td key={cell.batchId}>
                  <div><strong>{cell.mean}</strong> (n = {cell.n})</div>
                  <div style={{ fontSize: 12, color: '#5f6368' }}>range {cell.range} · sd {cell.sd} · 95% {cell.interval}</div>
                  {expanded === row.name && (
                    <ul style={{ margin: 0, paddingLeft: 18, fontSize: 12 }}>
                      {cell.values.map((v: any) => <li key={v.runId}>{String(v.value)}{' '}
                        <button type="button" onClick={() => onOpenRun(v.runId)}>run</button></li>)}
                    </ul>
                  )}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      <p style={{ fontSize: 12, color: '#5f6368' }}>Raw values per run: select a metric. Rates without a denominator are listed as not applicable, never as 0%.</p>
    </section>
  );
}
