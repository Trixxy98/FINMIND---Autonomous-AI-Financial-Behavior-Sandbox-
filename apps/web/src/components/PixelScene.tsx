import { useEffect, useMemo, useReducer, useState } from 'react';
import { eventsToCues, type Cue } from '../lib/cues.js';
import { SCENE, placePosition, sceneAt } from '../lib/scene.js';
import { SPRITES, spriteRects } from '../lib/sprites.js';
import { formatSen } from '../lib/money.js';
import { EvidenceBadge } from './EvidenceBadge.js';
import { SPEEDS, initialPlayback, playback, stepDelayMs } from '../lib/playback.js';

const P = SCENE.pixel;

function Sprite({ name, x, y, scale = P, title }: { name: string; x: number; y: number; scale?: number; title?: string }) {
  return (
    <g transform={`translate(${x} ${y})`}>
      {title && <title>{title}</title>}
      {spriteRects(SPRITES[name], scale).map((r, i) => <rect key={i} x={r.x} y={r.y} width={r.size} height={r.size} fill={r.fill} />)}
    </g>
  );
}

function Label({ x, y, text, max = 16 }: { x: number; y: number; text: string; max?: number }) {
  return <text x={x} y={y} fontSize={10} fontFamily="system-ui" fill="#2b2d42">{text.length > max ? `${text.slice(0, max - 1)}…` : text}<title>{text}</title></text>;
}

const PLACES: { place: string; sprite: string; label: string }[] = [
  { place: 'home', sprite: 'home', label: 'Home' },
  { place: 'mailbox', sprite: 'mailbox', label: 'Mailbox' },
  { place: 'desk', sprite: 'desk', label: 'Desk' },
  { place: 'counter', sprite: 'counter', label: 'Bank counter' },
  { place: 'vault', sprite: 'vault', label: 'Savings' },
  { place: 'postbox', sprite: 'postbox', label: 'Postbox' },
];

// ADR-0016: a picture of what was recorded. Figures come from replay; the timeline stays beside it.
export function SceneView({ events, cues, sequence, reducedMotion }:
  { events: readonly any[]; cues: readonly Cue[]; sequence: number; reducedMotion: boolean }) {
  const scene = useMemo(() => sceneAt(events, cues, sequence), [events, cues, sequence]);
  const merchantPlaces = scene.merchants.map((m) => m.place);
  const at = (place: string) => placePosition(place, merchantPlaces);
  const avatar = at(scene.avatarAt);
  const cue = scene.cue;
  const sunX = 40 + Math.min(scene.day ?? 0, 30) * 18;

  return (
    <figure style={{ margin: 0 }}>
      <svg viewBox={`0 0 ${SCENE.width} ${SCENE.height}`} width="100%" role="img" aria-label={cue?.caption ?? 'Scene'}
        style={{ maxWidth: 760, background: '#e7f5ff', imageRendering: 'pixelated', border: '1px solid #ddd' }}>
        <rect x={0} y={250} width={SCENE.width} height={110} fill="#d3f9d8" />
        <Sprite name="sun" x={sunX} y={20} title={`Day ${scene.day ?? '—'}`} />
        {PLACES.map((p) => { const { x, y } = at(p.place); return (
          <g key={p.place}><Sprite name={p.sprite} x={x} y={y} /><Label x={x} y={y - 4} text={p.label} /></g>); })}
        {cue?.kind === 'missing_door' && (() => { const { x, y } = at('missing_door'); return (
          <g><rect x={x} y={y} width={18} height={21} fill="none" stroke="#e03131" strokeDasharray="3 2" />
            <Label x={x - 10} y={y - 4} text="No such door" /></g>); })()}
        {scene.merchants.map((m) => { const { x, y } = at(m.place); return (
          <g key={m.place}><Sprite name="stall" x={x} y={y} /><Label x={x} y={y + 34} text={m.label.replace(' (synthetic)', '')} max={11} /></g>); })}
        {(cue?.kind === 'bill' || cue?.kind === 'message') && <Sprite name="letter" x={at('mailbox').x + 2} y={at('mailbox').y - 18} />}
        {cue?.kind === 'letter' && <Sprite name="letter" x={at('postbox').x + 2} y={at('postbox').y - 18} />}
        {cue?.kind === 'coins' && cue.from && cue.to && (() => {
          const a = at(cue.from), b = at(cue.to);
          // The coin travels from one account to the other; with reduced motion it is drawn at the destination.
          return (
            <g key={cue.sequence} transform={`translate(${b.x + 8} ${b.y - 20})`}>
              {!reducedMotion && <animateTransform attributeName="transform" type="translate" dur="0.7s" fill="freeze"
                from={`${a.x + 8} ${a.y - 20}`} to={`${b.x + 8} ${b.y - 20}`} />}
              <Sprite name="coin" x={0} y={0} title={cue.caption} />
            </g>);
        })()}
        <g style={{ transform: `translate(${avatar.x + 4}px, ${avatar.y + 30}px)`, transition: reducedMotion ? 'none' : 'transform 0.5s ease-in-out' }}>
          <Sprite name="avatar" x={0} y={0} title="The agent" />
        </g>
        {(cue?.kind === 'blocked' || cue?.kind === 'missing_door') && (
          <text x={avatar.x + 2} y={avatar.y + 26} fontSize={12} fontWeight={700} fill="#e03131">DENIED</text>
        )}
        {cue?.kind === 'speech' && (
          <g>
            {/* Above the buildings, with a pointer down to the avatar, so it never hides a place. */}
            <line x1={avatar.x + 16} y1={124} x2={avatar.x + 16} y2={avatar.y + 26} stroke="#f59f00" strokeDasharray="2 2" />
            <rect x={Math.max(10, Math.min(avatar.x - 120, SCENE.width - 410))} y={90} width={400} height={34} rx={4} fill="#fff9db" stroke="#f59f00" />
            <text x={Math.max(10, Math.min(avatar.x - 120, SCENE.width - 410)) + 6} y={104} fontSize={9} fontWeight={700} fill="#7a4f01">REPORTED BY AGENT</text>
            <text x={Math.max(10, Math.min(avatar.x - 120, SCENE.width - 410)) + 6} y={117} fontSize={10} fill="#2b2d42">
              {(cue.text ?? '').length > 72 ? `${cue.text!.slice(0, 71)}…` : cue.text}
            </text>
          </g>
        )}
        {scene.status && ['COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED'].includes(scene.status) && (
          <text x={SCENE.width - 200} y={30} fontSize={14} fontWeight={700} fill="#2b2d42">Run {scene.status}</text>
        )}
      </svg>
      <figcaption style={{ marginTop: 6 }}>
        <div><strong>Event {scene.sequence}</strong>: {cue ? <>{cue.evidence && <EvidenceBadge value={cue.evidence} />} {cue.caption}
          {cue.kind === 'speech' && <q> {cue.text}</q>}</> : 'Nothing to show for this event.'}</div>
        <div style={{ fontSize: 13 }}>Day {scene.day ?? '—'} · cash {formatSen(scene.cashSen)} · savings {formatSen(scene.savingsSen)} ·{' '}
          {scene.bills.filter((b) => !b.paid).length} unpaid bills <span style={{ color: '#5f6368' }}>(replayed from ledger events)</span></div>
      </figcaption>
    </figure>
  );
}

function usePrefersReducedMotion() {
  const query = useMemo(() => (typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null), []);
  const [reduced, setReduced] = useState(query?.matches ?? false);
  useEffect(() => {
    if (!query) return;
    const update = () => setReduced(query.matches);
    query.addEventListener('change', update);
    return () => query.removeEventListener('change', update);
  }, [query]);
  return reduced;
}

// Replay and live playback. The timeline, action detail, and ledger inspector stay one click away.
export function ScenePanel({ events, running, onOpenAction, onOpenLedger }: {
  events: readonly any[]; running: boolean; onOpenAction: (toolCallId: string) => void; onOpenLedger: () => void;
}) {
  const cues = useMemo(() => eventsToCues(events), [events]);
  const reducedMotion = usePrefersReducedMotion();
  const [state, dispatch] = useReducer(playback, undefined, () => initialPlayback(cues.length, running));
  const length = cues.length;

  useEffect(() => { if (!running) dispatch({ type: 'ended' }); }, [running]);
  useEffect(() => {
    if (!state.playing) return;
    const timer = setTimeout(() => dispatch({ type: 'tick', length }), stepDelayMs(state.speed));
    return () => clearTimeout(timer);
  }, [state.playing, state.index, state.speed, length]);

  const index = Math.min(state.index, Math.max(0, length - 1));
  const current = cues[index];
  if (!current) return <p>Nothing recorded yet.</p>;
  const nearby = cues.slice(Math.max(0, index - 6), index + 1);

  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>A picture of the recorded events ({length} cues, finmind-cues/v1). Coins move
        only for ledger commits; the agent's words are its own. The action list and ledger inspector remain the record.
        {reducedMotion && ' Reduced motion is on: positions change without animation.'}</p>
      <p>
        <button type="button" onClick={() => dispatch({ type: 'start' })}>Start</button>{' '}
        <button type="button" onClick={() => dispatch({ type: 'back', length })}>◀ Cue</button>{' '}
        {state.playing
          ? <button type="button" onClick={() => dispatch({ type: 'pause' })}>Pause</button>
          : <button type="button" onClick={() => dispatch({ type: 'play', length })}>Play</button>}{' '}
        <button type="button" onClick={() => dispatch({ type: 'forward', length })}>Cue ▶</button>{' '}
        {running && <button type="button" aria-pressed={state.live} onClick={() => dispatch({ type: 'live', length })}>Follow live</button>}{' '}
        <label>Speed{' '}
          <select value={state.speed} onChange={(e) => dispatch({ type: 'speed', speed: Number(e.target.value) })}>
            {SPEEDS.map((speed) => <option key={speed} value={speed}>{speed}×</option>)}
          </select>
        </label>{' '}
        <input type="range" aria-label="Cue position" min={0} max={length - 1} value={index}
          onChange={(e) => dispatch({ type: 'seek', index: Number(e.target.value), length })} style={{ width: 220, verticalAlign: 'middle' }} />{' '}
        cue {index + 1} of {length}{state.live ? ' · following live' : ''}
      </p>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <div style={{ flex: '3 1 480px' }}>
          <SceneView events={events} cues={cues} sequence={current.sequence} reducedMotion={reducedMotion} />
        </div>
        <aside style={{ flex: '1 1 220px', fontSize: 13 }} aria-label="Timeline">
          <strong>Timeline</strong>
          <ol style={{ paddingLeft: 18, margin: '4px 0' }}>
            {nearby.map((cue) => {
              const at = cues.indexOf(cue);
              return (
                <li key={cue.sequence} value={cue.sequence} style={{ fontWeight: at === index ? 700 : 400 }}>
                  <button type="button" onClick={() => dispatch({ type: 'seek', index: at, length })}
                    style={{ all: 'unset', cursor: 'pointer' }}>
                    {cue.evidence && <EvidenceBadge value={cue.evidence} />} {cue.caption}
                  </button>
                </li>);
            })}
          </ol>
          <p style={{ margin: '6px 0' }}>
            {current.toolCallId && <><button type="button" onClick={() => onOpenAction(current.toolCallId!)}>Action detail</button>{' '}</>}
            <button type="button" onClick={onOpenLedger}>Ledger inspector</button>
          </p>
        </aside>
      </div>
    </div>
  );
}
