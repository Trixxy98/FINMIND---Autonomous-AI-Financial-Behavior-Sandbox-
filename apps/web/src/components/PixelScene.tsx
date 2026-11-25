import { useMemo, useState } from 'react';
import { eventsToCues, type Cue } from '../lib/cues.js';
import { SCENE, placePosition, sceneAt } from '../lib/scene.js';
import { SPRITES, spriteRects } from '../lib/sprites.js';
import { formatSen } from '../lib/money.js';
import { EvidenceBadge } from './EvidenceBadge.js';

const P = SCENE.pixel;

function Sprite({ name, x, y, scale = P, title }: { name: string; x: number; y: number; scale?: number; title?: string }) {
  return (
    <g transform={`translate(${x} ${y})`}>
      {title && <title>{title}</title>}
      {spriteRects(SPRITES[name], scale).map((r, i) => <rect key={i} x={r.x} y={r.y} width={r.size} height={r.size} fill={r.fill} />)}
    </g>
  );
}

function Label({ x, y, text }: { x: number; y: number; text: string }) {
  return <text x={x} y={y} fontSize={10} fontFamily="system-ui" fill="#2b2d42">{text.length > 16 ? `${text.slice(0, 15)}…` : text}</text>;
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
export function SceneView({ events, cues, sequence }: { events: readonly any[]; cues: readonly Cue[]; sequence: number }) {
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
          <g key={m.place}><Sprite name="stall" x={x} y={y} /><Label x={x} y={y + 34} text={m.label.replace(' (synthetic)', '')} /></g>); })}
        {(cue?.kind === 'bill' || cue?.kind === 'message') && <Sprite name="letter" x={at('mailbox').x + 2} y={at('mailbox').y - 18} />}
        {cue?.kind === 'letter' && <Sprite name="letter" x={at('postbox').x + 2} y={at('postbox').y - 18} />}
        {cue?.kind === 'coins' && cue.from && cue.to && (() => {
          const a = at(cue.from), b = at(cue.to);
          return <Sprite name="coin" x={(a.x + b.x) / 2} y={Math.min(a.y, b.y) - 20} title={cue.caption} />;
        })()}
        <Sprite name="avatar" x={avatar.x + 4} y={avatar.y + 30} title="The agent" />
        {(cue?.kind === 'blocked' || cue?.kind === 'missing_door') && (
          <text x={avatar.x + 2} y={avatar.y + 26} fontSize={12} fontWeight={700} fill="#e03131">DENIED</text>
        )}
        {cue?.kind === 'speech' && (
          <g>
            <rect x={Math.min(avatar.x, SCENE.width - 260)} y={avatar.y - 30} width={250} height={34} rx={4} fill="#fff9db" stroke="#f59f00" />
            <text x={Math.min(avatar.x, SCENE.width - 260) + 6} y={avatar.y - 16} fontSize={9} fontWeight={700} fill="#7a4f01">REPORTED BY AGENT</text>
            <text x={Math.min(avatar.x, SCENE.width - 260) + 6} y={avatar.y - 4} fontSize={10} fill="#2b2d42">
              {(cue.text ?? '').length > 46 ? `${cue.text!.slice(0, 45)}…` : cue.text}
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

// Step 2: stepping cue by cue. Playback and live following arrive in step 3.
export function ScenePanel({ events }: { events: readonly any[] }) {
  const cues = useMemo(() => eventsToCues(events), [events]);
  const [index, setIndex] = useState(cues.length - 1);
  const current = cues[Math.max(0, Math.min(index, cues.length - 1))];
  if (!current) return <p>Nothing recorded yet.</p>;
  return (
    <div>
      <p style={{ fontSize: 12, color: '#5f6368' }}>A picture of the recorded events ({cues.length} cues, finmind-cues/v1). Coins move
        only for ledger commits; the agent's words are its own. The action list and ledger inspector remain the record.</p>
      <p>
        <button type="button" onClick={() => setIndex(0)}>Start</button>{' '}
        <button type="button" onClick={() => setIndex((i) => Math.max(0, i - 1))}>◀ Cue</button>{' '}
        <button type="button" onClick={() => setIndex((i) => Math.min(cues.length - 1, i + 1))}>Cue ▶</button>{' '}
        <button type="button" onClick={() => setIndex(cues.length - 1)}>End</button>{' '}
        cue {Math.min(index, cues.length - 1) + 1} of {cues.length}
      </p>
      <SceneView events={events} cues={cues} sequence={current.sequence} />
    </div>
  );
}
