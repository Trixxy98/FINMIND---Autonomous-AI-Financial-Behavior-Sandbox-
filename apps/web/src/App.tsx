import { useMemo, useState, type FormEvent } from 'react';
import { createApi } from './lib/api.js';
import { evidenceLabel } from './lib/evidence.js';

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
    <span style={{ ...TONE_STYLE[label.tone], padding: '1px 6px', borderRadius: 4, fontSize: 12, fontWeight: 600 }}>
      {label.text}
    </span>
  );
}

export function App() {
  // The token lives in component state only; reloading the page asks for it again.
  const [token, setToken] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [status, setStatus] = useState<string>('');
  const api = useMemo(() => createApi({ token: () => token, onUnauthorized: () => setToken(null) }), [token]);

  async function connect(event: FormEvent) {
    event.preventDefault();
    const candidate = draft.trim();
    setDraft('');
    const probe = createApi({ token: () => candidate });
    try {
      const models = await probe.listModels();
      setToken(candidate);
      setStatus(`Connected. ${models.models.length} models configured.`);
    } catch (error) {
      setStatus(error instanceof Error && error.message.includes('401') ? 'That token was not accepted.' : 'The API could not be reached.');
    }
  }

  if (!token) {
    return (
      <main style={{ fontFamily: 'system-ui', maxWidth: 480, margin: '48px auto' }}>
        <h1>FINMIND</h1>
        <p>Virtual-funds research sandbox. Enter the researcher token (FINMIND_RESEARCHER_TOKEN) to continue.</p>
        <form onSubmit={connect}>
          <label>
            Researcher token{' '}
            <input type="password" autoComplete="off" value={draft} onChange={(e) => setDraft(e.target.value)} />
          </label>{' '}
          <button type="submit" disabled={!draft.trim()}>Connect</button>
        </form>
        {status && <p role="status">{status}</p>}
      </main>
    );
  }

  return (
    <main style={{ fontFamily: 'system-ui', maxWidth: 960, margin: '24px auto' }}>
      <h1>FINMIND</h1>
      <p role="status">{status}</p>
      <p>
        Evidence labels: <EvidenceBadge value="proposed" /> <EvidenceBadge value="denied" />{' '}
        <EvidenceBadge value="committed" /> <EvidenceBadge value="reported_by_agent" />{' '}
        <EvidenceBadge value="verified_by_ledger" />
      </p>
      <button type="button" onClick={() => { setToken(null); setStatus(''); }}>Forget token</button>
      {/* Screens arrive in ADR-0014 steps 3 and 4. */}
      <span hidden>{String(Boolean(api))}</span>
    </main>
  );
}
