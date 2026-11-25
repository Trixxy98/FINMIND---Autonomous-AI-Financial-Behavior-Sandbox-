# FINMIND ADR 0016: Pixel-art Observatory View

- Status: Accepted 25 November 2026 (steps 1 and 2 implemented)
- Date: 25 November 2026
- Scope: an optional animated view of one run, driven only by recorded events

## Context

The specification (section 13.1) allows a small pixel-art view that makes run
activity easier to follow: an agent avatar dealing with a wallet, bills, and
savings. It sets limits: animations and speech bubbles come from recorded
events, not model narration; the view is presentation only and never the
authoritative ledger; proposed, denied, and committed actions are distinct;
agent claims are labelled apart from ledger-verified balances; the timeline,
action details, and ledger inspector stay available; and nothing implies that
a model is good or bad or turns outcomes into a game score.

ADR-0014 deferred this view until the screens passed the Phase 3 gate. ADR-0015
added read-only replay, which rebuilds a run's state from its events. This view
uses that same rebuilt state.

## Decision

### Source of truth

- A pure function turns recorded events into **cues**, one per event that has
  something to show. Each cue keeps its event sequence, so every animation can
  be traced to one event. Events without a cue show nothing.
- Every figure on the scene (cash, savings, day, bills) comes from the replay
  state, which is rebuilt from ledger events, and is captioned "replayed from
  ledger events". No figure comes from agent text.
- Agent statements (`submit_plan`, `finish`, model text) appear only as speech
  bubbles labelled REPORTED BY AGENT, quoting the recorded event.

### Event to animation (version 1)

| Recorded event | Animation | Caption label |
|---|---|---|
| `SCENARIO_MATERIALIZED`, `CLOCK_ADVANCED` | Day changes; sun moves | — |
| `OBLIGATION_CREATED` | A letter arrives in the mailbox | — |
| `SCENARIO_EVENT_DELIVERED` (message) | A note arrives; its sender is shown | — |
| `TOOL_CALL_PROPOSED` | The avatar walks to the place for that tool | PROPOSED |
| `LEDGER_COMMITTED` for that call | Coins move between the two accounts | COMMITTED |
| `TOOL_RETURNED`, denied or malformed | The avatar stops at a closed counter | DENIED |
| Unsupported capability | The avatar tries a door that does not exist and stops | DENIED |
| `request_funding` (ok) | The avatar posts a letter; no coins appear | REPORTED BY AGENT |
| `JOURNAL_REVERSED` | Coins move back | COMMITTED (reversal) |
| `RUN_STATUS_CHANGED` to a terminal status | The scene stops; the status is shown | — |

A denial is never drawn with coins moving, and a funding request never adds
coins. The mapping table is versioned (`finmind-cues/v1`).

### What the view does not do

- No score, points, levels, win or lose, and no expressions that judge the
  agent (no happy or sad faces). The avatar is neutral.
- It never replaces the timeline: the current event, its evidence label, and
  links to the action detail and ledger inspector stay beside the scene.

### Modes

- **Replay**: play, pause, speed, and step, over the same positions as the
  Replay tab.
- **Live**: follows the event stream while the run is going.

### Art and technology

- Original art only, drawn in code: each sprite is a small grid of palette
  indices in the repository, rendered as SVG rectangles. No external images,
  fonts, or assets, and nothing that copies the look of an existing game.
- No new dependency. Plain React and SVG.
- Every animation has a text caption, so the view works without seeing the
  motion. With `prefers-reduced-motion`, positions change without animation.

### Scope of version 1

One run at a time, one scene that fits every scenario: home, mailbox, bank
counter, savings vault, a row of merchants, and closed doors for capabilities
that do not exist. Several agents side by side is a later decision.

## Delivery

Each step ends with tests, a report, and a stop for review:

1. Cue mapping (pure function and tests).
2. Scene and sprites.
3. Playback (replay and live), captions, reduced motion, and a browser check.

#### Step 1 as implemented

- `apps/web/src/lib/cues.ts` (`finmind-cues/v1`) maps events to cues. A
  `replayed` outcome and an internal error get a caption that nothing new
  moved; a funding request's amount is not carried into the scene.
- Tests include 2,000 random event sequences: coins come only from
  `LEDGER_COMMITTED`, no denied cue moves coins, and the coins into and out of
  the wallet and vault add up to the replay balances.

#### Step 2 as implemented

- `apps/web/src/lib/sprites.ts`: original sprites as palette-key grids, one
  SVG rectangle per pixel. The avatar has two dot eyes and no mouth.
- `apps/web/src/lib/scene.ts`: fixed positions for every place, merchants in a
  row, and `sceneAt`, whose cash, savings, day, status, and bills come from
  `replayAt` at the same event.
- `apps/web/src/components/PixelScene.tsx` and a Scene tab on the run screen:
  stepping cue by cue, with the caption, evidence label, and replayed figures
  under the picture. Playback, live following, and reduced motion are step 3.
- The message cue shows a letter at the mailbox; the sender is in the caption
  only through the event key, since message payloads are not read here.

## Acceptance tests

- Every cue names an existing event, and an unknown event type produces none.
- No denied, malformed, or unsupported attempt produces a coin movement; a
  funding request never changes a balance on the scene.
- At every position, the figures on the scene equal the replay state, and at
  the last event they equal the ledger wallets.
- Agent text appears only in REPORTED BY AGENT bubbles.
- With reduced motion, nothing animates, and every caption is still shown.
- In the browser: a run can be watched live and replayed; the timeline and
  links stay visible throughout.

## Out of scope

Several agents in one scene, sound, scenario-specific scenery, and any
interaction that changes a run.

## Decisions to confirm

1. Art drawn in code by this project, unless the researcher provides their own
   original assets.
2. One run first; side-by-side agents later.
3. Both replay and live in version 1.
