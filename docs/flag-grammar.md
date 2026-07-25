# uniflag flag grammar — the second-generation signal language

**Status: adopted design constitution (2026-07-04); phases 1a–1e
implemented.** This document is the outcome of a full redesign interview and
is the normative spec for the live renderer: the Grammar renderer shipped,
the firmware fallback is the roaming ember, and the legacy painters and
corpora were retired (docs/effects-spec.md is historical, like the v1
`render/` crate docs). Phases 2–3 (§12 — the LMU and F1 adapters, each gated
on a live-verification session) remain open.

Codename **Grammar**: the C# implementation lives in
`plugin/src/Rendering/Grammar/` (`Uniflag.Rendering.Grammar` namespace); how
it is verified is §11. Nothing here touches the wire protocol: frames on the
wire don't care what painted them.

---

## 1. Goals, ranked

1. **Useful and instantly understandable while racing.** Peripheral-first: the
   identity and urgency of every signal must be decodable from colour + motion
   alone in peripheral vision. Glyphs and detail are a redundant glance layer.
2. **Maximum information without compromising (1).** Concurrent signals
   compose instead of masking each other; nothing the sim tells us is silently
   dropped.
3. **True to real life without compromising (1)–(2).** The panel mimics the
   real marshalling system — real flag colours, real board forms, no invented
   flags a discipline doesn't fly.
4. **Pretty, without compromising the above.**
5. **Consistent.** One design grammar; learning one flag teaches the others.
   Bespoke per-flag behaviour is banned unless licensed as a *signature* (§3).

**Target sims**: iRacing, Le Mans Ultimate, F1 (Codemasters/EA) first-class;
every other sim degrades gracefully through the generic unified-flag adapter.

## 2. The grammar — six rules

Every signal design in §6–§8 is *derived* from these rules; a change that
can't be derived needs a rule change first.

- **R1 — hue = identity.** Always the real-world flag colour. Hues are never
  repurposed. Idle states use only hues absent from the flag vocabulary
  (teal, violet, amber, achromatic grey).
- **R2 — motion rate = urgency**, on a fixed three-tier ladder (§4). The tier
  fixes the *rate*; a signal may keep a characteristic *pattern* at that rate
  (a **signature** — e.g. blue's sweep, green's onset sweep, the checkered
  scroll). Signatures also keep flags distinguishable under colour-deficient
  vision.
- **R3 — geometry = real-world form.** Field (full panel) = things that are
  cloth flags in real life. Board (centred chrome box, §8) = things that are
  physical boards/panels (SC/VSC/FCY boards, penalty notices, the start
  gantry). Frame (1-px perimeter accent) = synthetic advisories with no
  physical form.
- **R4 — universal onset transient.** Every signal *entry* opens with the
  same white flash (§4), scoped to its slot. Two licensed signature
  exceptions: green's onset **is** its sweep; red's onset **is** the flash it
  donated to everyone else.
- **R5 — motion is a transient, presence is the state.** The tier governs the
  onset, not the steady state: tier motion runs for one attention window,
  then the signal settles to its calm ambient form and stays — indefinitely —
  until the condition clears. Escalations re-arm the window; repeats don't
  nag. No signal may strobe for minutes.
- **R6 — composition over precedence.** Three slots (field, board, frame)
  render concurrently, max one winner per slot; precedence works *within*
  slots (§5). Red and checkered are field-exclusive takeovers.

## 3. Signature registry

The complete list of licensed bespoke patterns. Anything not listed renders
the plain tier motion.

| Signal | Signature |
|---|---|
| Blue field | wrapping 4-px bright sweep band (rate = tier) |
| Green field | onset light-band sweep (replaces the white flash) |
| Red field | the onset flash itself (donated to R4) |
| Black field | the white X scaffold |
| Meatball field | the orange disc |
| Debris field | diagonal yellow/red stripes |
| Checkered field | scrolling 4-px checker |
| Start gantry | animated light content on a static board |

## 4. Tiers and the envelope

**Tiers** (rates use `Anim.Strobe60`, i.e. the shipped ~60 % duty):

- **Tier 0 — ambient**: the signal's calm form. Cloth-wave (`Anim.WaveMult`)
  for fields, slow breathe (240-frame / 0.25 Hz) for accents and marks. This
  is also the *settled* form of every signal (R5).
- **Tier 1 — act soon**: 2 Hz **pulse** — alternation between the ambient
  rendering and the ambient rendering scaled to ~35 % (`ScaleRgb(·, 90)`).
  Modulates, never cuts to black.
- **Tier 2 — act now**: 4 Hz **strobe** — alternation between the ambient
  rendering and black. Cuts.

**The envelope** — one animation contract for every signal, all constants
universal:

| Phase | Frames (60 fps) | Rendering |
|---|---|---|
| Flash | 0–7 | 0–1: slot region solid white. 2–7: normal rendering blended toward white, weight `w = (7 − age) * 255 / 6`, per channel `c + (255 − c) * w / 255` |
| Attention | 8–299 | tier motion (T0 skips straight to ambient) |
| Ambient | 300+ | the signal's Tier-0 form, indefinitely |
| Fade-out | 15 frames | on clear: the old rendering scaled by `(15 − t) * 17`, ramping to whatever is underneath |

Envelope rules:

- **Entry** (slot content kind changes) → flash + fresh window.
- **Escalation** (same kind: tier increases, or the detail tuple *gains* —
  sector added, DT→SG, VSC→SC, more gantry lights) → flash + window re-arm.
- **De-escalation** (tier decreases or detail recedes) → no transient; the
  running window/epoch continues (or the settled state simply re-renders at
  the lower form).
- **Clear** (slot empties) → fade-out, never a flash. A *replacement*
  (new kind arrives in the same tick) shows the arriving signal's flash
  instead — the transient belongs to the arriving signal, not the departing
  one. Red → green (restart) therefore sweeps; red → none fades.
- The window epoch is per-slot; a board arriving does not restart the field's
  envelope.

## 5. Slots, precedence, suppression

**Field slot** (exactly one): precedence
`Red > Yellow > Black > Meatball > Blue > White > Checkered > Green > Debris > none`.
Safety outranks driver-directed orders because of the demotion rule:

- **Demotion rule**: when Black or Meatball is active but loses the field,
  it renders as a **board** instead (`DT`/`SG`/`DQ` when detail is known;
  otherwise the X-glyph board / disc-icon board). Nothing is silently
  dropped.

**Board slot** (at most one): precedence
`SC > VSC > FCY > DQ > SG > DT > demoted black (X) > demoted meatball (disc)
> time penalty (+N) > countdown (10/5) > start gantry`.

**Frame slot** (at most one): `furled > incident-limit`. Because furled
clears and incident persists, incident re-emerges after a furled window —
without re-arming its own blink (no state change).

**Takeovers and suppression**:

- **Red**: field-exclusive — suppresses board, frame, and sector strip
  (session stopped; extra signalling is noise).
- **Checkered**: field-exclusive — suppresses board, frame, and strip (race
  over; orders moot).
- **DQ**: suppresses frame advisories (your race is over) but keeps the board
  slot (it *is* a board) and paints the steady X field.
- **Disconnected**: the firmware fallback owns the panel (§7); the plugin
  renders nothing.

**Paint order**: field → board → frame → sector strip. The strip paints last
and owns its two rows outright; a perimeter frame missing its bottom-centre
pixels still reads as a frame.

## 6. Signal catalogue

Shared palette (LED-tuned, carried over): `YELLOW (255,220,0)`,
`BLUE (0,64,255)`, `RED (255,0,0)`, `GREEN (0,220,0)`, `WHITE (255,255,255)`,
`ORANGE (255,90,0)`, `SECTOR_DIM (40,30,0)`; new: `TEAL (0,255,192)`,
`VIOLET (176,0,255)`, `AMBER (255,120,8)`.

### 6.1 Fields

| Field | Ambient (T0 / settled) | Notes |
|---|---|---|
| Yellow | cloth-wave `(150,255)` | Tier from source severity: displayed→T0, waving→T1, double-waved / "be prepared to stop"→T2. Wave levels are dead as a render concept — they are tier inputs |
| Red | cloth-wave `(150,255)` | Enters at T2 (4 Hz strobe through the window), settles to calm red. Total takeover |
| Green | cloth-wave `(220,255)` | Onset = signature sweep: 30 frames, band half-width 4, `pos = age * 40 / 30 − 4`, in-band `(200,255,200)`; then T1 for the window remainder |
| Blue | cloth-wave `(150,255)` | T1: sweep band 1 px / 2 frames; T2: 1 px / frame. Band = 4 px, multiplier 255, wrapping (`FloorMod`) |
| White | cloth-wave `(220,255)` | Final lap (iRacing). Enters T0–T1 per source; one attention pulse, nothing frantic |
| Black | black + white X (`|x−y| ≤ 1` or `|x+y−31| ≤ 1`) | X ambient: breathe 240 mapped to 80..180 (brighter + calmer than legacy). T1 window: X pulses 2 Hz between 255 and 90. **DQ variant**: steady X at 200, no motion, + `DQ` board |
| Meatball | black + orange disc | Disc: half-pixel metric `dx2 = 2x−31, dy2 = 2y−31`, lit iff `dx2² + dy2² ≤ 400` (r = 10.0). Ambient: disc breathes 150..255; T1 window: 2 Hz pulse. The real flag's true form — replaces the legacy rotating quadrants |
| Checkered | scrolling checker, `off = frame/8` | Attention window: `off = frame/2` (4× scroll), settling to the lazy drift. Field-exclusive |
| Debris | diagonal stripes: `(FloorDiv(x + y + off, 4) & 1)` → YELLOW/RED, `off = frame/16` | The real surface flag, promoted from idle-arm board to a true field. Enters T0, lowest precedence |

### 6.2 Boards (all wear the §8 chrome; all static — motion lives in the field)

| Board | Glyphs | Producer |
|---|---|---|
| `SC` | S·C | iRacing caution bits (a full-course caution *is* a pace car); LMU phase 6 + pace car; F1 status 1 |
| `VSC` | V·S·C | F1 status 2; LMU if distinguishable |
| `FCY` | F·C·Y | LMU FCY-without-SC |
| `DT` / `SG` / `DQ` | D·T / S·G / D·Q | LMU `mPenalties`, F1 penalty events, iRacing `disqualify` (DQ) |
| Demoted black | single X glyph | demotion rule, §5 |
| Demoted meatball | disc icon (9×9) | demotion rule, §5 |
| Time penalty | `+N` (e.g. +5, +10) | F1/LMU steward notices; board-only, no field change |
| Countdown | `10` / `5` | iRacing `tenToGo`/`fiveToGo` |
| Start gantry | 5 lights, 4×4 px each, 1-px gaps (24 px row) | see §6.4 |

### 6.3 Frames (1-px perimeter)

| Frame | Look | Envelope |
|---|---|---|
| Incident-limit | solid RED ring | enters T1 (2 Hz blink) for the window, settles to steady `ScaleRgb(RED, 60)` |
| Furled warning | alternating 4-px white dashes around the ring (dash = `(ringPos >> 2) & 1`) | same envelope; settled dashes at `ScaleRgb(WHITE, 60)` |

### 6.4 Start sequence

Gantry board (chrome per §8, lights as content):

- **Ready** (iRacing `startReady`/`oneLapToGreen`, F1 formation lap): all five
  lights amber standby breathe (`80 + Breathe(f,240)*80/255`, range 80..160 —
  board content must read at a glance, unlike the idle-dim envelope).
- **Set**: lights solid RED — `StartLightsLit` N of 5 left-to-right when the
  sim provides a count (LMU `mStartLight`/`mNumRedLights`), all five otherwise
  (iRacing `startSet`).
- **Go**: all lights out; the green flag takes the field naturally (iRacing
  raises the green bit at go).

`crossed` (halfway) stays unmapped — future candidate, no glyph invented.

### 6.5 Sector strip

Bottom 2 rows (y = 30, 31), segments S1 x 0..=9 / S2 11..=20 / S3 22..=31,
gap columns 10 and 21 untouched. Shown for *local* yellows with known
locality; never for full-course states; never under red or checkered.
Active segment: attention window pulses at the field's tier rate (2 Hz at
T0/T1, 4 Hz at T2), settling to steady cloth-wave YELLOW `(180,255)`;
inactive segments constant `SECTOR_DIM`. Painted last (§5).

## 7. Idle states — the Watchline Embers family

One primitive — the **ember**: a bright core, shoulders at `(m*3) >> 3`, a
1-px halo above at `m >> 2` — parameterised four ways. Inset rule: no idle
pixel ever touches row/column 0 or 31 (structurally distinct from frames).
Salience strictly decreases as the system gets healthier. Kills: the hard
100 ms blink, the green centred orb, and the orb's two-stage fallback —
one look per state, no shape-shifting idles.

| State | Design |
|---|---|
| (a) **Fallback** — device powered, no host (firmware) | Roaming amber ember, rows 29–30, columns 11–21. Triangle glide: `P = 480; t = f % 480; u = t < 240 ? t : 479 − t; pos16 = 192 + u*112/239; x0 = pos16 >> 4; fr = (pos16 & 15) * 17`. Row 30 weights: `x0−1: 9*(255−fr)/255`, `x0: (24*(255−fr)+9*fr)/255`, `x0+1: (9*(255−fr)+24*fr)/255`, `x0+2: 9*fr/255`; row 29 halo: `x0: 6*(255−fr)/255`, `x0+1: 6*fr/255`. Base AMBER via `ScaleRgb`. Constant luminance, sub-pixel crossfade, LUT-free — honest to `screens.rs`'s embedded-literal style |
| (b) **Connected-idle** — stream alive, no game | Docked teal beacon: cores (15,30),(16,30) at `m = 10 + Breathe(f,240)*14/255`; shoulders (14,30),(17,30) at `(m*3)>>3`; halos (15,29),(16,29) at `m>>2`. Base TEAL |
| (c1) **Race idle** — game live, Racing/Paused, no signal | **Fully static**: (1,30)=(8,8,8), (2,30)=(4,4,4), (29,30)=(4,4,4), (30,30)=(8,8,8). Zero motion — safe because a dead stream self-reveals via the firmware's 1.5 s fallback timeout |
| (c2) **Session idle** — game live, other session, no signal | Violet flankers at x=8 and x=23: core (x,30), shoulders (x±1,30), halo (x,29); `mL = 8 + Breathe(f,240)*12/255`, `mR` same with `f+120`. Exact anti-phase (`SinU8[p] + SinU8[p+128] == 256`): aggregate luminance constant to ±1 LSB |

State (a) requires a firmware release and a rewrite of effects-spec §7a; it
ships with phase 1d. With start telemetry present, pre-race sessions show the
gantry standby instead of (c2) — the flankers are the no-telemetry fallback.

## 8. Board chrome and glyphs

One chrome for every board: **solid black backing, 1-px white outline, white
7×11 glyphs**, centred both axes. Uniform glyph gap 1, interior padding 2,
outline 1 → two-glyph boards 21 px wide, three-glyph 29 px, height 19
(y 6..24), leaving ≥ 6 field rows above and below. Boards are static; they
appear with a 2-frame white-box onset (§4) and then sit. The gantry is the
one licensed animated-content exception (§3). The breathing yellow caution
border of the legacy SC/VSC board dies — the yellow field carries the mood.

Glyph inventory (7×11 grid): existing `V S C` + new `F Y D T G Q`, digits
`0–9`, `+`, the X glyph, and the 9×9 disc icon.

## 9. State model — `SignalState`

Adapters are pure telemetry→state functions; the renderer diffs successive
states to run envelopes. No age/animation fields cross the adapter boundary.

```
Flag        None | Yellow | Blue | White | Red | Green | Checkered | Debris
            (the winning TRACK-STATE flag, adapter priority; C#: TrackFlag)
Tier        0 Ambient | 1 Alert | 2 Urgent      (urgency of Flag; replaces WaveLevel)
BlackFlag   bool                        (black-family order — orthogonal, see below)
BlackDetail None | DriveThrough | StopAndGo | Disqualified
Meatball    bool                        (mechanical flag — orthogonal, see below)
Session     PreRace | Racing | Paused | PostRace | Replay | Unknown   (unchanged)
Caution     None | VirtualSafetyCar | SafetyCar | FullCourseYellow
Sectors     SectorSet                  (unchanged)
StartPhase  Off | Ready | Set | Go
StartLightsLit      byte 0..5          (0 = derive from phase)
TimePenaltySeconds  byte               (0 = none)
CountdownLaps       byte               (0 = none; else 10 / 5)
Furled              bool
IncidentWarning     bool
```

Black and Meatball are **orthogonal** to the track flag rather than values of
it: the demotion rule (§5) must see them even while another flag wins the
field — folding them into `Flag` would force adapters to discard exactly the
concurrency the compositor exists to preserve. Debris stays inside `Flag`
(lowest track state — when it loses, the winner already conveys caution).

Deleted from the legacy model: `WaveLevel` (→ `Tier`), `Slowdown` (no
producer anywhere — iRacing's slow-down meter is not in telemetry),
`Debris` bool (→ `Flag`), `Flag.Orange`/`Flag.Black` (→ the orthogonal
`Meatball`/`BlackFlag` dimensions).

## 10. Adapter contracts

**Generic** (all sims, unchanged posture): unified flags → fields; yellow
enters at Tier 1 (the marshal-is-waving guess), everything else Tier 0;
`Flag_Orange` → Meatball; session mapping and the no-game predicate carry
over verbatim; caution/sectors/boards/penalty detail only from refiners.

**iRacing** (refiner, updated in phase 1): red→Red T2 · yellow/yellowWaving→
Yellow T0/T1 · caution/cautionWaving→Yellow field T1/T2 + SC board ·
black→Black T1 · disqualify→Black + Disqualified · repair→Meatball T1 ·
furled→Furled · debris→Debris T0 · blue→Blue T1 (keeping SimHub's
`blue && !green` suppression) · white→White T0 · green/greenHeld→Green T1 ·
startReady/oneLapToGreen→Ready, startSet→Set, startGo→Go ·
tenToGo/fiveToGo→CountdownLaps (new) · incident count vs limit−margin→
IncidentWarning.

**LMU** (phase 2, new; every mapping live-verified before trust — this doc
has been burned by prose-only rF2 claims before): `mYellowFlagState`
severity→tiers · `mSectorFlag[0..2]`→Sectors · phase 6 split into FCY vs SC
via `mYellowFlagState`/pace-car fields · `mStartLight`/`mNumRedLights`→
Set + StartLightsLit · `mPenalties`→DT/SG.

**F1** (phase 3, new; live-verified): `m_safetyCarStatus` 1→SC, 2→VSC,
3→Ready (formation) · marshal zones aggregated to thirds→Sectors, zone
severity→tier · penalty events→DT/SG/TimePenaltySeconds.

## 11. Conformance strategy

**The renderer has no byte corpus.** `testdata/frames-grammar/` existed
briefly and was retired: pinning 41 binaries made every deliberate visual
tweak a 41-file regeneration, and the diff a reviewer saw was
`Binary files differ` — churn bought with unreviewable commits. While the
design is still moving, byte-exactness is the wrong contract for the
painters. (The bytes live in git history if a future freeze wants them.)

What replaces it:

- **The scenario catalogue** (`plugin/tools/ScenarioCatalogue.cs`) survives
  the corpus and is the durable asset: ~40 curated scripts covering the
  signal vocabulary, each a `{ name, description, script: [{frame, state}, …],
  sample_frame }` replayed from frame 0 (the envelope makes rendering a
  function of state *history*, so the script — not a lone state — is the unit).
  Sample frames are chosen to discriminate: strobe phases, breathe peaks,
  sweep positions, flash blend weights, fade depths.
- **A smoke pass** (`GrammarSmokeTests`) replays every catalogue scenario and
  asserts only what stays true across visual tuning: no throw, a whole 3072-byte
  frame, deterministic output, a survivable window either side of the sample
  frame, and darkness exactly where darkness is the signal. It catches
  compositor/envelope interaction bugs that per-painter unit tests miss, and it
  never needs regenerating.
- **Per-painter unit tests** (`GrammarPainterTests`, `GrammarCompositorTests`,
  `GrammarEnvelopeTests`) keep asserting specific pixels and specific
  slot/phase decisions — targeted, hand-authored, and cheap to update when a
  decision deliberately changes.
- **Visual review is a tool, not a test.** `just frames-sheet` renders the whole
  catalogue to one labelled contact sheet and `just frames <scenario>` renders
  a single frame to PNG, so "does this still look right" is answered by looking.
- **Timelines** (`testdata/timelines/`) remain the adapter-side contract:
  C#-only, hand-authored, schema-revved in deliberate commits.
- **The wire protocol keeps its golden vectors.** `testdata/proto/` is frozen
  and does not churn — that contract is about bytes, so bytes are the right
  fixture. Nothing here changes it.
- No user-facing legacy/Grammar toggle: the redesign replaces.

## 12. Implementation plan

Phases are individually shippable; the repo stays green after every step.

- **1a — foundation** (no behaviour change): `SignalState`, `Compositor`
  (slot selection + demotion + suppression), `EnvelopeTracker` (per-slot
  diffing, phases, escalation rules) — all pure and unit-tested. Legacy
  renderer untouched.
- **1b — painters**: palette + glyph additions, board chrome, field/board/
  frame painters, sector strip, Watchline idles (b)–(c2), all against the
  paint-target abstraction; golden scripts + first `frames-grammar/` corpus;
  ASCII/PNG contact sheet for human review of the regen.
- **1c — cutover**: `RendererLoop` dispatches through Grammar; generic +
  iRacing adapters emit `SignalState`; preview tour rebuilt on the new
  vocabulary; timelines schema-rev; docs updated (this doc becomes normative;
  effects-spec marked historical).
- **1d — firmware fallback**: `screens.rs` roaming ember + §7a rewrite +
  firmware release (rides the same release as the plugin cutover).
- **1e — retirement**: delete legacy painters, `testdata/frames/`,
  `GoldenFrameTests`; CLAUDE.md + docs updated to name the Grammar corpus as
  the conformance contract.
- **2 — LMU adapter**: mappings per §10, gated on a live-verification
  session; sector strip, FCY board, gantry counts, DT/SG become reachable.
- **3 — F1 adapter**: mappings per §10, live-verified; VSC, formation,
  time-penalty boards become reachable.

## 13. Decision log

Adopted 2026-07-04 in a full design interview: peripheral-first with glance
layer · iRacing/LMU/F1 focus · rules R1–R6 · form-based geometry (blue stays
a field; meatball is the disc) · caution family = tiered yellow field +
regime boards + sector strip · penalty family incl. slowdown cut, DQ
terminal, time-penalty boards · white = final-lap only (no synthesized last
lap) · start family incl. LMU light counts + countdown boards, `crossed`
unmapped · frame-slot language (red ring / white dashes, furled > incident)
· attention-decay envelope (5 s window, universal) · Watchline Embers idles
(workflow-judged, composite) · uniform board chrome · white-flash onset +
fade-out clears · staged corpus replacement · `SignalState` model +
demotion rule · phased adapters iRacing-first.
