// ADR-0016: where things are, and what the scene shows at one event. Figures come from replay only.
import type { Cue, Place } from './cues.js';
import { replayAt, type Bill } from './replay.js';

export const SCENE = { width: 640, height: 360, pixel: 3 };

const FIXED: Record<string, { x: number; y: number }> = {
  home: { x: 30, y: 170 },
  mailbox: { x: 120, y: 200 },
  desk: { x: 190, y: 210 },
  counter: { x: 280, y: 190 },
  vault: { x: 380, y: 185 },
  postbox: { x: 460, y: 200 },
  missing_door: { x: 560, y: 190 },
  treasury: { x: 560, y: 40 },
};

// Cash travels with the avatar's home; merchants stand in a row along the bottom.
export function placePosition(place: Place, merchants: readonly string[]): { x: number; y: number } {
  if (place === 'wallet') return FIXED.home;
  if (FIXED[place]) return FIXED[place];
  const index = merchants.indexOf(place);
  return { x: 20 + Math.max(index, 0) * 76, y: 295 };
}

export interface SceneState {
  sequence: number;
  cashSen: number;
  savingsSen: number;
  day: number | null;
  status: string | null;
  bills: Bill[];
  avatarAt: Place;
  cue: Cue | null;
  merchants: { place: string; label: string }[];
}

export function sceneAt(events: readonly any[], cues: readonly Cue[], sequence: number): SceneState {
  const replay = replayAt(events, sequence);
  let avatarAt: Place = 'home';
  for (const cue of cues) {
    if (cue.sequence > sequence) break;
    if ((cue.kind === 'walk' || cue.kind === 'blocked' || cue.kind === 'missing_door' || cue.kind === 'letter') && cue.place) avatarAt = cue.place;
    if (cue.kind === 'end') avatarAt = 'home';
  }
  const merchants = events
    .filter((e) => e.sequence <= sequence && e.eventType === 'ACCOUNT_CREATED' && e.payload?.kind === 'merchant')
    .map((e) => ({ place: `merchant:${e.payload.accountId}`, label: e.payload.label ?? 'Merchant' }));
  return {
    sequence: replay.sequence, cashSen: replay.cashSen, savingsSen: replay.savingsSen, day: replay.day, status: replay.status,
    bills: replay.bills, avatarAt, cue: cues.find((c) => c.sequence === sequence) ?? null, merchants,
  };
}
