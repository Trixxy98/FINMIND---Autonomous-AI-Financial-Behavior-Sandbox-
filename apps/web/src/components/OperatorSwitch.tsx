import { useCallback, useEffect, useState, type FormEvent } from 'react';
import type { createApi } from '../lib/api.js';

type Api = ReturnType<typeof createApi>;

// ADR-0017: one switch that cancels every active run and refuses new starts until resumed. Both need a reason.
export function OperatorSwitch({ api }: { api: Api }) {
  const [state, setState] = useState<{ stopped: boolean; reason: string | null; since: string | null } | null>(null);
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  const [message, setMessage] = useState('');

  const load = useCallback(async () => {
    try { setState(await api.operatorStatus()); } catch { setState(null); }
  }, [api]);
  useEffect(() => { void load(); const timer = setInterval(() => void load(), 15_000); return () => clearInterval(timer); }, [load]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      if (state?.stopped) {
        await api.resumeAll(reason.trim());
        setMessage('Runs can be started again.');
      } else {
        const result = await api.stopAll(reason.trim());
        setMessage(`Stopped. ${result.cancelledRunIds.length} active run(s) cancelled.`);
      }
      setReason('');
      setAsking(false);
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'The request failed.');
    }
  }

  if (!state) return null;
  return (
    <div role={state.stopped ? 'alert' : undefined} style={{ margin: '8px 0', padding: state.stopped ? 8 : 0,
      background: state.stopped ? '#fce8e6' : undefined, borderRadius: 6, fontSize: 14 }}>
      {state.stopped && <strong>All runs are stopped by the operator: {state.reason}. Starts and claims are refused. </strong>}
      {!asking && (
        <button type="button" onClick={() => { setAsking(true); setMessage(''); }}>
          {state.stopped ? 'Resume runs…' : 'Stop all runs…'}
        </button>
      )}
      {asking && (
        <form onSubmit={submit} style={{ display: 'inline' }}>
          <label>{state.stopped ? 'Reason to resume' : 'Reason to stop every active run'}{' '}
            <input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} size={40} />
          </label>{' '}
          <button type="submit" disabled={!reason.trim()}>{state.stopped ? 'Resume' : 'Stop all runs'}</button>{' '}
          <button type="button" onClick={() => setAsking(false)}>Cancel</button>
        </form>
      )}
      {message && <span role="status"> {message}</span>}
    </div>
  );
}
