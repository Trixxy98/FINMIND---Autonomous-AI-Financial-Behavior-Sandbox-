import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { SPEEDS, initialPlayback, playback, stepDelayMs } from '../build-test/lib/playback.js';

const run = (state, ...actions) => actions.reduce(playback, state);

describe('playback', () => {
  it('a finished run opens paused at the last cue; a live run opens following the newest cue', () => {
    assert.deepEqual(initialPlayback(5, false), { index: 4, playing: false, live: false, speed: 1 });
    assert.deepEqual(initialPlayback(5, true), { index: 4, playing: true, live: true, speed: 1 });
    assert.equal(initialPlayback(0, false).index, 0);
  });

  it('play from the start steps one cue per tick and pauses at the end of a finished run', () => {
    let s = run(initialPlayback(3, false), { type: 'start' }, { type: 'play', length: 3 });
    assert.deepEqual([s.index, s.playing], [0, true]);
    s = run(s, { type: 'tick', length: 3 });
    assert.equal(s.index, 1);
    s = run(s, { type: 'tick', length: 3 }, { type: 'tick', length: 3 });
    assert.deepEqual([s.index, s.playing], [2, false]);
  });

  it('play at the end of a finished run starts again from the first cue', () => {
    const s = run(initialPlayback(3, false), { type: 'play', length: 3 });
    assert.deepEqual([s.index, s.playing], [0, true]);
  });

  it('live waits at the newest cue and continues when new cues arrive', () => {
    let s = initialPlayback(2, true);
    s = run(s, { type: 'tick', length: 2 });
    assert.deepEqual([s.index, s.playing, s.live], [1, true, true]); // waiting, not paused
    s = run(s, { type: 'tick', length: 4 });
    assert.equal(s.index, 2); // one cue at a time, so each is seen
    s = run(s, { type: 'ended' }, { type: 'tick', length: 4 }, { type: 'tick', length: 4 });
    assert.deepEqual([s.index, s.playing, s.live], [3, false, false]);
  });

  it('stepping or pausing stops following; going live jumps to the newest cue', () => {
    let s = run(initialPlayback(5, true), { type: 'back', length: 5 });
    assert.deepEqual([s.index, s.playing, s.live], [3, false, false]);
    s = run(s, { type: 'tick', length: 9 });
    assert.equal(s.index, 3); // paused: new cues do not move it
    s = run(s, { type: 'live', length: 9 });
    assert.deepEqual([s.index, s.playing, s.live], [8, true, true]);
    s = run(s, { type: 'pause' });
    assert.deepEqual([s.playing, s.live], [false, false]);
  });

  it('positions stay within the cues and speed changes the delay only', () => {
    let s = run(initialPlayback(3, false), { type: 'forward', length: 3 }, { type: 'forward', length: 3 });
    assert.equal(s.index, 2);
    s = run(s, { type: 'start' }, { type: 'back', length: 3 });
    assert.equal(s.index, 0);
    s = run(s, { type: 'seek', index: 99, length: 3 });
    assert.equal(s.index, 2);
    assert.deepEqual(SPEEDS, [0.5, 1, 2, 4]);
    assert.equal(stepDelayMs(1), 900);
    assert.equal(stepDelayMs(4), 225);
    s = run(s, { type: 'speed', speed: 4 });
    assert.deepEqual([s.index, s.speed], [2, 4]);
    s = run(s, { type: 'speed', speed: 3 });
    assert.equal(s.speed, 4); // not an offered speed
  });
});
