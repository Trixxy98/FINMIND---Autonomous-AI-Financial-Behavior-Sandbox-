import { useCallback, useEffect, useRef, useState } from 'react';
import type { Api } from '../lib/api.js';
import { formatSen } from '../lib/money.js';
import { streamRunEvents } from '../lib/sse.js';
import { budgetLines, isTerminal, modelTextFor, refreshesOn } from '../lib/runView.js';
import { EvidenceBadge } from '../components/EvidenceBadge.js';
import { MetricsPanel } from '../components/MetricsPanel.js';
import { ClassificationsPanel } from '../components/ClassificationsPanel.js';
import { ReplayPanel } from '../components/ReplayPanel.js';
import { ScenePanel } from '../components/PixelScene.js';

interface Props { api: Api; token: string; runId: string; onBack: () => void; onUnauthorized: () => void }
type Tab = 'actions' | 'ledger' | 'events' | 'metrics' | 'review' | 'replay' | 'scene';

const box = { border: '1px solid #ddd', borderRadius: 6, padding: 12, marginBottom: 12 } as const;
const pre = { background: '#f8f9fa', padding: 8, overflowX: 'auto', fontSize: 12, maxHeight: 300, whiteSpace: 'pre-wrap', wordBreak: 'break-word' } as const;

async function all(fetchPage: (after: number) => Promise<any>, key: string): Promise<any[]> {
  const items: any[] = [];
  let after = 0;
  for (;;) {
    const page = await fetchPage(after);
    items.push(...page[key]);
    if (page[key].length === 0 || page.nextAfterSequence === after) return items;
    after = page.nextAfterSequence;
  }
}

// Agent text is shown as quoted text only. The browser never reads a number out of it (ADR-0014).
function AgentText({ text }: { text: string | null | undefined }) {
  if (!text) return <span style={{ color: '#5f6368' }}>No text.</span>;
  return (
    <blockquote style={{ margin: 0, padding: '4px 8px', borderLeft: '3px solid #f9ab00', background: '#fef7e0', whiteSpace: 'pre-wrap' }}>
      <EvidenceBadge value="reported_by_agent" /> {text}
    </blockquote>
  );
}

export function RunScreen({ api, token, runId, onBack, onUnauthorized }: Props) {
  const [run, setRun] = useState<any>(null);
  const [wallets, setWallets] = useState<any>(null);
  const [actions, setActions] = useState<any[]>([]);
  const [events, setEvents] = useState<any[]>([]);
  const [journals, setJournals] = useState<any[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('actions');
  const [streamState, setStreamState] = useState('connecting');
  const [verify, setVerify] = useState<any>(null);
  const [metrics, setMetrics] = useState<any>(null);
  const [cancelling, setCancelling] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const ledgerOpen = useRef(false);

  const refresh = useCallback(async () => {
    try {
      const [detail, walletView, actionList] = await Promise.all([
        api.getRun(runId), api.wallets(runId), all((after) => api.actions(runId, after, 500), 'actions'),
      ]);
      setRun(detail);
      setWallets(walletView);
      setActions(actionList);
      if (ledgerOpen.current) setJournals(await all((after) => api.ledger(runId, after, 500), 'journals'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the run.');
    }
  }, [api, runId]);

  const refreshSoon = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => void refresh(), 400);
  }, [refresh]);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      await refresh();
      // Events are read from the start, then followed live; the stream resumes after any drop.
      let loaded: any[] = [];
      try {
        loaded = await all((after) => api.events(runId, after, 500), 'events');
        setEvents(loaded);
      } catch { /* shown by refresh */ }
      setStreamState('live');
      try {
        const end = await streamRunEvents({
          runId, token, signal: controller.signal, afterSequence: loaded.at(-1)?.sequence ?? 0,
          onEvent: (event) => {
            setEvents((current) => (current.some((e) => e.sequence === event.sequence) ? current : [...current, event]));
            if (refreshesOn(event.eventType)) refreshSoon();
          },
          onReconnect: () => { setStreamState('live (reconnected)'); refreshSoon(); },
          onUnauthorized,
        });
        setStreamState(`ended: ${end.status ?? 'development run'}, last event ${end.lastSequence}`);
        void refresh();
      } catch (e) {
        if (!controller.signal.aborted) setStreamState(`stopped: ${e instanceof Error ? e.message : 'error'}`);
      }
    })();
    return () => { controller.abort(); if (timer.current) clearTimeout(timer.current); };
  }, [api, runId, token, refresh, refreshSoon, onUnauthorized]);

  async function openLedger() {
    setTab('ledger');
    ledgerOpen.current = true;
    setJournals(await all((after) => api.ledger(runId, after, 500), 'journals'));
  }

  async function cancel() {
    try {
      await api.cancelRun(runId, reason.trim());
      setCancelling(false);
      setReason('');
      void refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Cancel failed.');
    }
  }

  if (!run) return <section><p>{error || 'Loading run…'}</p><button type="button" onClick={onBack}>Back to runs</button></section>;
  const action = actions.find((a) => a.toolCallId === selected);
  const model = action ? modelTextFor(events, action.toolCallId) : null;
  const counts = run.actionCounts;

  return (
    <section>
      <p><button type="button" onClick={onBack}>Back to runs</button></p>
      <h2 style={{ marginBottom: 4 }}>Run {run.runId}</h2>
      <p style={{ marginTop: 0 }}>
        <strong>{run.status ?? 'no lifecycle'}</strong>{run.statusReason && ` — ${run.statusReason}`} · {run.scenarioId ?? '—'} ·{' '}
        {run.agent ? `${run.agent.provider} / ${run.agent.modelId}` : '—'} · day {run.virtualDay ?? '—'} of {run.durationDays ?? '—'} ·{' '}
        {run.researchEligible ? 'research run' : <strong>development run, excluded from results</strong>} · stream: {streamState}
      </p>
      {error && <p role="alert" style={{ color: '#a50e0e' }}>{error}</p>}

      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        <div style={{ ...box, flex: '1 1 280px' }}>
          <h3 style={{ marginTop: 0 }}>Wallets</h3>
          {wallets?.accounts.map((w: any) => (
            <div key={w.accountId}>
              {w.kind}: <strong>{formatSen(w.balanceSen)}</strong> <EvidenceBadge value={w.evidence} />
            </div>
          ))}
          {wallets && !wallets.reconciled && (
            <p role="alert" style={{ color: '#a50e0e' }}>
              Not reconciled: postings and events disagree.{' '}
              {wallets.accounts.map((w: any) => `${w.kind} ${formatSen(w.balanceSen)} vs ${formatSen(w.eventBalanceSen)}`).join('; ')}
            </p>
          )}
          {wallets && <div style={{ fontSize: 12, color: '#5f6368' }}>As of event {wallets.asOfSequence}{wallets.reconciled ? ', reconciled' : ''}</div>}
        </div>
        <div style={{ ...box, flex: '1 1 280px' }}>
          <h3 style={{ marginTop: 0 }}>Budgets</h3>
          {budgetLines(run.budgets).map((line) => <div key={line}>{line}</div>)}
        </div>
        <div style={{ ...box, flex: '1 1 280px' }}>
          <h3 style={{ marginTop: 0 }}>Actions by evidence</h3>
          {['committed', 'denied', 'reported_by_agent', 'proposed'].map((label) => (
            <div key={label}><EvidenceBadge value={label} /> {counts[label]} of {counts.total}</div>
          ))}
          {run.managed && !isTerminal(run.status) && run.status !== 'DRAFT' && (
            cancelling ? (
              <div style={{ marginTop: 8 }}>
                <label>Reason (recorded){' '}<input value={reason} onChange={(e) => setReason(e.target.value)} /></label>{' '}
                <button type="button" disabled={!reason.trim()} onClick={() => void cancel()}>Confirm cancel</button>{' '}
                <button type="button" onClick={() => setCancelling(false)}>Keep running</button>
              </div>
            ) : <p><button type="button" onClick={() => setCancelling(true)}>Cancel run…</button></p>
          )}
        </div>
      </div>

      <nav style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
        <button type="button" aria-pressed={tab === 'actions'} onClick={() => setTab('actions')}>Actions ({actions.length})</button>
        <button type="button" aria-pressed={tab === 'ledger'} onClick={() => void openLedger()}>Ledger inspector</button>
        <button type="button" aria-pressed={tab === 'events'} onClick={() => setTab('events')}>Events ({events.length})</button>
        <button type="button" aria-pressed={tab === 'metrics'} onClick={async () => { setTab('metrics'); setMetrics(await api.metrics(runId)); }}>Metrics</button>
        <button type="button" aria-pressed={tab === 'review'} onClick={() => setTab('review')}>Review</button>
        <button type="button" aria-pressed={tab === 'replay'} onClick={() => setTab('replay')}>Replay</button>
        <button type="button" aria-pressed={tab === 'scene'} onClick={() => setTab('scene')}>Scene</button>
        <span style={{ marginLeft: 'auto' }}>
          Export:{' '}
          {(['jsonl', 'csv'] as const).map((format) => (
            <button key={format} type="button" onClick={async () => {
              // Fetched with the Authorization header and saved from a blob, so the token never enters a URL.
              const url = URL.createObjectURL(await api.exportRun(runId, format));
              const link = document.createElement('a');
              link.href = url;
              link.download = `finmind-run-${runId}.${format}`;
              link.click();
              URL.revokeObjectURL(url);
            }}>{format.toUpperCase()}</button>
          ))}
        </span>
      </nav>

      {tab === 'actions' && (
        <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
          <table style={{ borderCollapse: 'collapse', fontSize: 13, flex: '1 1 50%' }}>
            <thead><tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}><th>#</th><th>Call</th><th>Tool</th><th>Evidence</th><th>Outcome</th></tr></thead>
            <tbody>
              {actions.map((a) => (
                <tr key={a.toolCallId} onClick={() => setSelected(a.toolCallId)} style={{ cursor: 'pointer', borderBottom: '1px solid #eee', background: a.toolCallId === selected ? '#e8f0fe' : undefined }}>
                  <td>{a.proposedSequence}</td><td>{a.toolCallId}</td><td>{a.tool}</td><td><EvidenceBadge value={a.evidence} /></td>
                  <td>{a.complete ? `${a.outcome}${a.reasonCode ? ` (${a.reasonCode})` : ''}` : 'no outcome yet'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ ...box, flex: '1 1 50%' }}>
            {!action ? <p>Select an action to see its detail.</p> : (
              <>
                <h3 style={{ marginTop: 0 }}>{action.tool} <EvidenceBadge value={action.evidence} /></h3>
                <p>Call {action.toolCallId}, proposed at event {action.proposedSequence}
                  {action.returnedSequence ? `, returned at event ${action.returnedSequence}` : ', no outcome yet'}.</p>
                <p>Decision: <strong>{action.outcome ?? 'none yet'}</strong>{action.reasonCode && ` — ${action.reasonCode}`}</p>
                {action.journalId && <p>Journal: <button type="button" onClick={() => void openLedger()}>{action.journalId}</button></p>}
                <h4>Model text for this turn</h4>
                <AgentText text={model?.outputText} />
                <h4>Arguments the agent proposed (redacted)</h4>
                <pre style={pre}>{JSON.stringify(action.arguments, null, 2)}</pre>
                <h4>Response the agent received</h4>
                <pre style={pre}>{action.response ? JSON.stringify(action.response, null, 2) : 'None recorded.'}</pre>
              </>
            )}
          </div>
        </div>
      )}

      {tab === 'ledger' && (
        <div>
          <p>
            <button type="button" onClick={async () => setVerify(await api.verify(runId))}>Verify event chain</button>{' '}
            {verify && (verify.intact ? 'Event chain intact.' : <strong style={{ color: '#a50e0e' }}>Event chain broken at event {verify.brokenAtSequence}.</strong>)}
            {' '}{wallets && (wallets.reconciled ? 'Wallets reconcile with ledger events.' : <strong style={{ color: '#a50e0e' }}>Wallets do not reconcile.</strong>)}
          </p>
          {!journals ? <p>Loading ledger…</p> : (
            <table style={{ borderCollapse: 'collapse', fontSize: 13, width: '100%' }}>
              <thead><tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}><th>Event</th><th>Journal</th><th>Kind</th><th>Postings</th><th>Status</th></tr></thead>
              <tbody>
                {journals.map((j) => (
                  <tr key={j.journalId} style={{ borderBottom: '1px solid #eee', verticalAlign: 'top', background: action?.journalId === j.journalId ? '#e8f0fe' : undefined }}>
                    <td>{j.commitSequence ?? 'before event log'}</td>
                    <td style={{ fontFamily: 'monospace', fontSize: 11 }}>{j.journalId}</td>
                    <td>{j.kind}{j.reversesJournalId && <div style={{ fontSize: 11 }}>reverses {j.reversesJournalId}{j.reversalReason && ` (${j.reversalReason})`}</div>}</td>
                    <td>{j.postings.map((p: any) => (
                      <div key={p.accountId}>{p.label ?? p.accountKind}: {formatSen(p.amountSen)}</div>
                    ))}</td>
                    <td><EvidenceBadge value={j.evidence} /> {j.effective ? 'effective' : <strong>reversed by {j.reversedByJournalId}</strong>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'review' && <ClassificationsPanel api={api} runId={runId} />}
      {tab === 'replay' && <ReplayPanel key={events.length} events={events} wallets={wallets} />}
      {tab === 'scene' && <ScenePanel key={events.length} events={events} />}

      {tab === 'metrics' && (metrics ? <MetricsPanel metrics={metrics} /> : <p>Loading metrics…</p>)}

      {tab === 'events' && (
        <table style={{ borderCollapse: 'collapse', fontSize: 12, width: '100%' }}>
          <thead><tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}><th>#</th><th>Type</th><th>Source</th><th>Operation</th><th>Outcome</th><th>Reason</th><th>Trace</th></tr></thead>
          <tbody>
            {events.map((e) => (
              <tr key={e.sequence} style={{ borderBottom: '1px solid #f1f3f4' }}>
                <td>{e.sequence}</td><td>{e.eventType}</td><td>{e.source}</td><td>{e.operation ?? ''}</td><td>{e.outcome ?? ''}</td><td>{e.reasonCode ?? ''}</td><td>{e.traceId ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
