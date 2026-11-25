import { CATEGORY_LABELS, outcomeValue, type Outcome } from '../lib/outcomes.js';
import { formatSen } from '../lib/money.js';

// ADR-0018: outcomes as facts from the ledger, each with its exposure and the events behind it. No score.
export function OutcomesPanel({ outcomes }: { outcomes: any }) {
  const list: (Outcome & { id: string; definition: string; evidence: number[]; perRecipient?: Record<string, number> })[] =
    outcomes.outcomes ?? [];
  if (list.length === 0) return <p>No outcomes for this run. {outcomes.exposedNote}</p>;
  const categories = Object.keys(CATEGORY_LABELS).filter((c) => list.some((o) => o.category === c));
  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>{outcomes.outcomesVersion} · {outcomes.scenarioVersion}. Facts from the ledger and the
        record, not judgements. "Not exposed" means the run ended before the situation arose. There is no total or score.</p>
      {categories.map((category) => (
        <section key={category}>
          <h3 style={{ fontSize: 15, marginBottom: 4 }}>{CATEGORY_LABELS[category]}</h3>
          <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 13 }}>
            <tbody>
              {list.filter((o) => o.category === category).map((o) => (
                <tr key={o.id} style={{ borderBottom: '1px solid #eee', verticalAlign: 'top' }}>
                  <td style={{ width: '38%' }}><code>{o.id}</code><div style={{ fontSize: 12, color: '#5f6368' }}>{o.definition}</div></td>
                  <td style={{ width: '22%' }}><strong style={{ color: o.exposed ? undefined : '#5f6368' }}>{outcomeValue(o)}</strong>
                    {o.exposed && o.perRecipient && Object.keys(o.perRecipient).length > 0 && (
                      <ul style={{ margin: 0, paddingLeft: 16, fontSize: 12 }}>
                        {Object.entries(o.perRecipient).map(([who, sen]) => <li key={who}>{who}: {formatSen(sen)}</li>)}
                      </ul>
                    )}
                  </td>
                  <td style={{ fontSize: 12 }}>{o.evidence.length ? `events ${o.evidence.join(', ')}` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ))}
    </div>
  );
}
