// ADR-0016 step 3: playback over cues. Replay steps through a finished run; live follows new cues one at a time.
export const SPEEDS = [0.5, 1, 2, 4] as const;
export const stepDelayMs = (speed: number) => Math.round(900 / speed);

export interface Playback { index: number; playing: boolean; live: boolean; speed: number }

export type PlaybackAction =
  | { type: 'tick'; length: number }
  | { type: 'play'; length: number } | { type: 'pause' } | { type: 'start' } | { type: 'ended' }
  | { type: 'back'; length: number } | { type: 'forward'; length: number }
  | { type: 'seek'; index: number; length: number }
  | { type: 'live'; length: number }
  | { type: 'speed'; speed: number };

const last = (length: number) => Math.max(0, length - 1);
const clamp = (index: number, length: number) => Math.min(Math.max(0, index), last(length));

export const initialPlayback = (length: number, running: boolean): Playback =>
  ({ index: last(length), playing: running, live: running, speed: 1 });

export function playback(state: Playback, action: PlaybackAction): Playback {
  switch (action.type) {
    case 'tick':
      if (!state.playing) return state;
      if (state.index < last(action.length)) return { ...state, index: state.index + 1 };
      return state.live ? state : { ...state, playing: false }; // live waits at the newest cue
    case 'play':
      return { ...state, playing: true, index: !state.live && state.index >= last(action.length) ? 0 : state.index };
    case 'pause': return { ...state, playing: false, live: false };
    case 'ended': return { ...state, live: false }; // finish the cues already received, then pause
    case 'start': return { ...state, index: 0, playing: false, live: false };
    case 'back': return { ...state, index: clamp(state.index - 1, action.length), playing: false, live: false };
    case 'forward': return { ...state, index: clamp(state.index + 1, action.length), playing: false, live: false };
    case 'seek': return { ...state, index: clamp(action.index, action.length), playing: false, live: false };
    case 'live': return { ...state, index: last(action.length), playing: true, live: true };
    case 'speed': return (SPEEDS as readonly number[]).includes(action.speed) ? { ...state, speed: action.speed } : state;
  }
}
