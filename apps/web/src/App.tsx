import { useCallback, useMemo, useState, type FormEvent } from 'react';
import { createApi } from './lib/api.js';
import { formFromSnapshot, type RunForm, type Scenario } from './lib/runForm.js';
import { RunsScreen } from './screens/RunsScreen.js';
import { NewRunScreen } from './screens/NewRunScreen.js';
import { RunScreen } from './screens/RunScreen.js';
import { BatchesScreen } from './screens/BatchesScreen.js';
import { CompareScreen } from './screens/CompareScreen.js';
import { ReviewScreen } from './screens/ReviewScreen.js';
import { OperatorSwitch } from './components/OperatorSwitch.js';

type View = { name: 'runs' } | { name: 'new'; copied?: { form: RunForm; warnings: string[] } } | { name: 'run'; runId: string }
  | { name: 'batches' } | { name: 'newBatch' } | { name: 'compare'; batchIds: string[] } | { name: 'review' };

export function App() {
  // The token lives in component state only; reloading the page asks for it again (ADR-0014).
  const [token, setToken] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [status, setStatus] = useState('');
  const [view, setView] = useState<View>({ name: 'runs' });
  const unauthorized = useCallback(() => { setToken(null); setStatus('The token was not accepted. Enter it again.'); }, []);
  const api = useMemo(() => createApi({ token: () => token, onUnauthorized: unauthorized }), [token, unauthorized]);

  async function connect(event: FormEvent) {
    event.preventDefault();
    const candidate = draft.trim();
    setDraft('');
    try {
      await createApi({ token: () => candidate }).listModels();
      setToken(candidate);
      setStatus('');
      setView({ name: 'runs' });
    } catch (error) {
      setStatus(error instanceof Error && error.message.includes('401') ? 'That token was not accepted.' : 'The API could not be reached.');
    }
  }

  async function copy(snapshot: any) {
    const scenarios = (await api.listScenarios()).scenarios.filter((s: Scenario) => s.latest);
    setView({ name: 'new', copied: formFromSnapshot(snapshot, scenarios) });
  }

  if (!token) {
    return (
      <main style={{ fontFamily: 'system-ui', maxWidth: 480, margin: '48px auto' }}>
        <h1>FINMIND</h1>
        <p>Virtual-funds research sandbox. Enter the researcher token (FINMIND_RESEARCHER_TOKEN) to continue.</p>
        <form onSubmit={connect}>
          <label>Researcher token{' '}
            <input type="password" autoComplete="off" value={draft} onChange={(e) => setDraft(e.target.value)} />
          </label>{' '}
          <button type="submit" disabled={!draft.trim()}>Connect</button>
        </form>
        {status && <p role="status">{status}</p>}
      </main>
    );
  }

  return (
    <main style={{ fontFamily: 'system-ui', maxWidth: 1200, margin: '16px auto', padding: '0 16px' }}>
      <header style={{ display: 'flex', gap: 12, alignItems: 'baseline', borderBottom: '1px solid #ddd' }}>
        <h1 style={{ fontSize: 22 }}>FINMIND</h1>
        <span style={{ color: '#5f6368' }}>Virtual funds only</span>
        <nav style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
          <button type="button" onClick={() => setView({ name: 'runs' })}>Runs</button>
          <button type="button" onClick={() => setView({ name: 'new' })}>New run</button>
          <button type="button" onClick={() => setView({ name: 'batches' })}>Batches</button>
          <button type="button" onClick={() => setView({ name: 'review' })}>Review</button>
          <button type="button" onClick={() => { setToken(null); setStatus(''); }}>Forget token</button>
        </nav>
      </header>
      <OperatorSwitch api={api} />
      {view.name === 'runs' && (
        <RunsScreen api={api} onNew={() => setView({ name: 'new' })} onOpen={(runId) => setView({ name: 'run', runId })}
          onCopy={(snapshot) => void copy(snapshot)} />
      )}
      {view.name === 'new' && (
        <NewRunScreen key={JSON.stringify(view.copied ?? null)} api={api} copied={view.copied}
          onCreated={(runId) => setView({ name: 'run', runId })} onCancel={() => setView({ name: 'runs' })} />
      )}
      {view.name === 'review' && <ReviewScreen api={api} onOpenRun={(runId) => setView({ name: 'run', runId })} />}
      {view.name === 'batches' && (
        <BatchesScreen api={api} onNewBatch={() => setView({ name: 'newBatch' })}
          onOpenRun={(runId) => setView({ name: 'run', runId })} onCompare={(batchIds) => setView({ name: 'compare', batchIds })} />
      )}
      {view.name === 'newBatch' && (
        <NewRunScreen api={api} batch onCreated={() => setView({ name: 'batches' })} onCancel={() => setView({ name: 'batches' })} />
      )}
      {view.name === 'compare' && (
        <CompareScreen api={api} batchIds={view.batchIds} onBack={() => setView({ name: 'batches' })}
          onOpenRun={(runId) => setView({ name: 'run', runId })} />
      )}
      {view.name === 'run' && (
        <RunScreen key={view.runId} api={api} token={token} runId={view.runId} onBack={() => setView({ name: 'runs' })}
          onUnauthorized={unauthorized} />
      )}
    </main>
  );
}
