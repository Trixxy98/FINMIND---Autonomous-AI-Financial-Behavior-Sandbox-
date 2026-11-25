import { useMemo, useState } from 'react';
import { formatSen } from '../lib/money.js';
import { replayAt } from '../lib/replay.js';

// ADR-0015: read-only replay of the recorded events. It writes nothing and calls no model.
export function ReplayPanel({ events, wallets }: { events: any[]; wallets: any }) {
  const [index, setIndex] = useState(events.length);
  const sequence = index === 0 ? 0 : events[index - 1].sequence;
  const state = useMemo(() => replayAt(events, sequence), [events, sequence]);
  const atEnd = index === events.length;
  const ledger = (kind: string) => wallets?.accounts.find((a: any) => a.kind === kind)?.balanceSen ?? 0;
  const matches = atEnd && wallets && ledger('cash') === state.cashSen && ledger('savings') === state.savingsSen;
  const nextDay = () => {
    const from = events.findIndex((e, i) => i >= index && e.eventType === 'CLOCK_ADVANCED');
    setIndex(from < 0 ? events.length : from + 1);
  };

  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>Replay rebuilds the state from the recorded events, in order. It shows what was
        recorded; it does not run the agent again.</p>
      <p>
        <button type="button" onClick={() => setIndex(0)}>Start</button>{' '}
        <button type="button" onClick={() => setIndex((i) => Math.max(0, i - 1))}>◀ Event</button>{' '}
        <button type="button" onClick={() => setIndex((i) => Math.min(events.length, i + 1))}>Event ▶</button>{' '}
        <button type="button" onClick={nextDay}>Next day ▶▶</button>{' '}
        <button type="button" onClick={() => setIndex(events.length)}>End</button>{' '}
        <input type="range" aria-label="Event position" min={0} max={events.length} value={index}
          onChange={(e) => setIndex(Number(e.target.value))} style={{ width: 320, verticalAlign: 'middle' }} />{' '}
        event {sequence} of {events.at(-1)?.sequence ?? 0}
      </p>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 8, flex: '1 1 260px' }}>
          <div>Status: <strong>{state.status ?? '—'}</strong> · day {state.day ?? '—'}</div>
          <div>Cash: <strong>{formatSen(state.cashSen)}</strong> · savings: <strong>{formatSen(state.savingsSen)}</strong></div>
          <div style={{ fontSize: 12, color: '#5f6368' }}>Replayed from ledger events.</div>
          {atEnd && wallets && (matches
            ? <div style={{ color: '#0d652d' }}>At the last event this matches the ledger wallets.</div>
            : <div role="alert" style={{ color: '#a50e0e' }}>At the last event this does not match the ledger wallets.</div>)}
        </div>
        <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 8, flex: '1 1 260px' }}>
          <div>Action: {state.currentAction
            ? <><strong>{state.currentAction.tool}</strong> ({state.currentAction.toolCallId}) → {state.currentAction.outcome ?? 'no outcome yet'}
              {state.currentAction.reasonCode ? ` (${state.currentAction.reasonCode})` : ''}</>
            : '—'}</div>
          <div>Outcomes so far: {Object.entries(state.outcomes).map(([k, n]) => `${k} ${n}`).join(', ') || '—'}</div>
          <div>Last event: {state.lastEvent ? `${state.lastEvent.sequence} ${state.lastEvent.eventType}` : '—'}</div>
        </div>
        <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 8, flex: '1 1 260px' }}>
          <div>Bills:</div>
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {state.bills.map((b) => <li key={b.obligationId}>{b.label ?? b.obligationId}: {formatSen(b.amountSen)}, due day {b.dueDay ?? '—'} —{' '}
              {b.paid ? 'paid' : 'unpaid'}</li>)}
          </ul>
          <div>Messages delivered: {state.messages.map((m) => `${m.key} (day ${m.day})`).join(', ') || 'none'}</div>
        </div>
      </div>
    </div>
  );
}
