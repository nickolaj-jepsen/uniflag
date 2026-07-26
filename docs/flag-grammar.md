# uniflag flag grammar — the second-generation signal language

**The renderer's design doc** — and a working one. This is a prerelease
hobby project, the visual design is still moving, and none of it is settled
enough to argue from. What's worth keeping is the *consistency*: the six
rules in §2 are there so the panel reads as one language. If a new signal
doesn't fall out of them, that usually means a rule wants revisiting — which
is fine, it's just worth doing on purpose rather than by accident.

Codename **Grammar**: the C# implementation lives in
`plugin/core/Rendering/Grammar/` (`Uniflag.Rendering.Grammar` namespace); how
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
   Bespoke per-flag behaviour needs a reason — the ones that have earned it
   are collected as *signatures* in §3.

**Target sims**: iRacing, Le Mans Ultimate, F1 (Codemasters/EA) get the most
attention; every other sim degrades gracefully through the generic
unified-flag adapter.

## 2. The grammar — six rules

Everything in §6–§8 falls out of these rules. When something doesn't, that's
the interesting case — revisit the rule rather than bolting an exception onto
the side of it.

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
  physical boards/panels (the SC and DQ boards, lap-countdown notices, the
  start gantry). Frame (1-px perimeter accent) = synthetic advisories with no
  physical form.
- **R4 — universal onset transient.** Every signal *entry* opens with the
  same white flash (§4), scoped to its slot. Two signature exceptions: green's onset **is** its sweep; red's onset **is** the flash it
  donated to everyone else.
- **R5 — motion is a transient, presence is the state.** The tier governs the
  onset, not the steady state: tier motion runs for one attention window,
  then the signal settles to its calm ambient form and stays — indefinitely —
  until the condition clears. Escalations re-arm the window; repeats don't
  nag. No signal may strobe for minutes.
- **R6 — composition over precedence.** Three slots (field, board, frame)
  render concurrently, max one winner per slot; precedence works *within*
  slots (§5). Red and checkered are field-exclusive takeovers.

## 3. Signatures

The bespoke patterns that have earned an exception, kept in one list so the
exceptions stay countable. Anything not here renders the plain tier motion.

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
  bare black → DQ, gantry Ready → Set) → flash + window re-arm.
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
  it renders as a **board** instead (`DQ` when disqualified; otherwise the
  X-glyph board / disc-icon board). Nothing is silently dropped.

**Board slot** (at most one): precedence
`SC > DQ > demoted black (X) > demoted meatball (disc) > countdown (10/5)
> start gantry`.

**Frame slot** (at most one): `furled > incident-limit`. Because furled
clears and incident persists, incident re-emerges after a furled window —
without re-arming its own blink (no state change).

**Takeovers and suppression**:

- **Red**: field-exclusive — suppresses board and frame (session stopped;
  extra signalling is noise).
- **Checkered**: field-exclusive — suppresses board and frame (race over;
  orders moot).
- **DQ**: suppresses frame advisories (your race is over) but keeps the board
  slot (it *is* a board) and paints the steady X field.
- **Disconnected**: the firmware fallback owns the panel (§7); the plugin
  renders nothing.

**Paint order**: field → board → frame.

## 6. Signal catalogue

Shared palette (LED-tuned): `YELLOW (255,220,0)`, `BLUE (0,64,255)`,
`RED (255,0,0)`, `GREEN (0,220,0)`, `WHITE (255,255,255)`,
`ORANGE (255,90,0)`, `TEAL (0,255,192)`, `VIOLET (176,0,255)`,
`AMBER (255,120,8)`.

### 6.1 Fields

| Field | Ambient (T0 / settled) | Notes |
|---|---|---|
| Yellow | cloth-wave `(150,255)` | Tier from source severity: displayed→T0, waving→T1, waving full-course caution→T2 — wave level is a tier input, never a render concept |
| Red | cloth-wave `(150,255)` | Enters at T2 (4 Hz strobe through the window), settles to calm red. Total takeover |
| Green | cloth-wave `(220,255)` | Onset = signature sweep: 30 frames, band half-width 4, `pos = age * 40 / 30 − 4`, in-band `(200,255,200)`; then T1 for the window remainder |
| Blue | cloth-wave `(150,255)` | T1: sweep band 1 px / 2 frames; T2: 1 px / frame. Band = 4 px, multiplier 255, wrapping (`FloorMod`) |
| White | cloth-wave `(220,255)` | Final lap (iRacing). Enters T0–T1 per source; one attention pulse, nothing frantic |
| Black | black + white X (`|x−y| ≤ 1` or `|x+y−31| ≤ 1`) | X ambient: breathe 240 mapped to 80..180. T1 window: X pulses 2 Hz between 255 and 90. **DQ variant**: steady X at 200, no motion, + `DQ` board |
| Meatball | black + orange disc | Disc: half-pixel metric `dx2 = 2x−31, dy2 = 2y−31`, lit iff `dx2² + dy2² ≤ 400` (r = 10.0). Ambient: disc breathes 150..255; T1 window: 2 Hz pulse. The real flag's form |
| Checkered | scrolling checker, `off = frame/8` | Attention window: `off = frame/2` (4× scroll), settling to the lazy drift. Field-exclusive |
| Debris | diagonal stripes: `((x + y + off) / 4 & 1)` → YELLOW/RED, `off = frame/16` | Enters T0, lowest precedence |

### 6.2 Boards (all wear the §8 chrome; all static — motion lives in the field)

| Board | Glyphs | Producer |
|---|---|---|
| `SC` | S·C | iRacing caution bits (a full-course caution *is* a pace car) |
| `DQ` | D·Q | iRacing `disqualify` |
| Demoted black | single X glyph | demotion rule, §5 |
| Demoted meatball | disc icon (9×9) | demotion rule, §5 |
| Countdown | `10` / `5` | iRacing `tenToGo`/`fiveToGo` |
| Start gantry | 5 lights, 4×4 px each, 1-px gaps (24 px row) | see §6.4 |

### 6.3 Frames (1-px perimeter)

| Frame | Look | Envelope |
|---|---|---|
| Incident-limit | solid RED ring | enters T1 (2 Hz blink) for the window, settles to steady `ScaleRgb(RED, 60)` |
| Furled warning | alternating 4-px white dashes around the ring (dash = `(ringPos >> 2) & 1`) | same envelope; settled dashes at `ScaleRgb(WHITE, 60)` |

### 6.4 Start sequence

Gantry board (chrome per §8, lights as content):

- **Ready** (iRacing `startReady`/`oneLapToGreen`): all five lights amber
  standby breathe (`80 + Breathe(f,240)*80/255`, range 80..160 — board
  content must read at a glance, unlike the idle-dim envelope).
- **Set** (iRacing `startSet`/`greenHeld`): all five lights solid RED. No
  sim uniflag ships an adapter for exposes a per-light count, so there is no
  N-of-5 rendering.
- **Go**: all lights out; the green flag takes the field naturally (iRacing
  raises the green bit at go).

`crossed` (halfway) stays unmapped — future candidate, no glyph invented.

## 7. Idle states — the Watchline Embers family

One primitive — the **ember**: a bright core, shoulders at `(m*3) >> 3`, a
1-px halo above at `m >> 2` — parameterised four ways. Inset rule: no idle
pixel ever touches row/column 0 or 31 (structurally distinct from frames).
Salience strictly decreases as the system gets healthier. One look per
state: an idle never shape-shifts partway through.

| State | Design |
|---|---|
| (a) **Fallback** — device powered, no host (firmware) | Roaming amber ember, rows 29–30, columns 11–21. Triangle glide: `P = 480; t = f % 480; u = t < 240 ? t : 479 − t; pos16 = 192 + u*112/239; x0 = pos16 >> 4; fr = (pos16 & 15) * 17`. Row 30 weights: `x0−1: 9*(255−fr)/255`, `x0: (24*(255−fr)+9*fr)/255`, `x0+1: (9*(255−fr)+24*fr)/255`, `x0+2: 9*fr/255`; row 29 halo: `x0: 6*(255−fr)/255`, `x0+1: 6*fr/255`. Base AMBER via `ScaleRgb`. Constant luminance, sub-pixel crossfade, LUT-free — honest to `screens.rs`'s embedded-literal style |
| (b) **Connected-idle** — stream alive, no game | Docked teal beacon: cores (15,30),(16,30) at `m = 10 + Breathe(f,240)*14/255`; shoulders (14,30),(17,30) at `(m*3)>>3`; halos (15,29),(16,29) at `m>>2`. Base TEAL |
| (c1) **Race idle** — game live, Racing/Paused, no signal | **Fully static**: (1,30)=(8,8,8), (2,30)=(4,4,4), (29,30)=(4,4,4), (30,30)=(8,8,8). Zero motion — safe because a dead stream self-reveals via the firmware's 1.5 s fallback timeout |
| (c2) **Session idle** — game live, other session, no signal | Violet flankers at x=8 and x=23: core (x,30), shoulders (x±1,30), halo (x,29); `mL = 8 + Breathe(f,240)*12/255`, `mR` same with `f+120`. Exact anti-phase (`SinU8[p] + SinU8[p+128] == 256`): aggregate luminance constant to ±1 LSB |

State (a) is the only one the firmware paints, so changing it needs a
firmware release. With start telemetry present, pre-race sessions show the
gantry standby instead of (c2) — the flankers are the no-telemetry fallback.

## 8. Board chrome and glyphs

One chrome for every board: **solid black backing, 1-px white outline, white
7×11 glyphs**, centred both axes. Uniform glyph gap 1, interior padding 2,
outline 1 → two-glyph boards 21 px wide, three-glyph 29 px, height 19
(y 6..24), leaving ≥ 6 field rows above and below. Boards are static; they
appear with a 2-frame white-box onset (§4) and then sit. The gantry is the
the one board with animated content (§3). Boards carry no coloured
border of their own — the field behind them carries the mood.

Glyph inventory (7×11 grid): `S C D Q`, digits `0 1 5`, the X glyph, and the
9×9 disc icon — exactly what the shipped boards spell (`SC`, `DQ`, `10`/`5`,
the demoted X, the demoted disc). Draw new glyphs when a refiner needs a new
word.

## 9. State model — `SignalState`

Adapters are pure telemetry→state functions; the renderer diffs successive
states to run envelopes. No age/animation fields cross the adapter boundary.

```
Flag        None | Yellow | Blue | White | Red | Green | Checkered | Debris
            (the winning TRACK-STATE flag, adapter priority; C#: TrackFlag)
Tier        0 Ambient | 1 Alert | 2 Urgent      (urgency of Flag; replaces WaveLevel)
BlackFlag   bool                        (black-family order — orthogonal, see below)
Disqualified bool                       (terminal black-family order; steady X + DQ board)
Meatball    bool                        (mechanical flag — orthogonal, see below)
Session     PreRace | Racing | Paused | Unknown
SafetyCar   bool                        (full-course caution: pace car out)
StartPhase  Off | Ready | Set | Go
CountdownLaps       byte               (0 = none; else 10 / 5)
Furled              bool
IncidentWarning     bool
```

Every field here is one a shipped adapter can set. Add a dimension when a
refiner is about to produce it, not before: an unreachable field costs
painters, glyphs and tests that no telemetry can exercise.

Black and Meatball are **orthogonal** to the track flag rather than values of
it: the demotion rule (§5) must see them even while another flag wins the
field — folding them into `Flag` would force adapters to discard exactly the
concurrency the compositor exists to preserve. Debris stays inside `Flag`
(lowest track state — when it loses, the winner already conveys caution).

There is deliberately no slowdown field: no sim exposes one (iRacing's
slow-down meter is not in telemetry).

## 10. Adapter contracts

**Generic** (all sims): unified flags → fields; yellow enters at Tier 1
(the marshal-is-waving guess), everything else Tier 0; `Flag_Orange` →
Meatball; session mapping and the no-game predicate as documented in
`plugin/core/Adapters/GenericAdapter.cs`; safety car, DQ, start sequence and
notices only from refiners.

**iRacing** (refiner): red→Red T2 · yellow/yellowWaving→
Yellow T0/T1 · caution/cautionWaving→Yellow field T1/T2 + SC board ·
black→Black T1 · disqualify→Black + Disqualified · repair→Meatball T1 ·
furled→Furled · debris→Debris T0 · blue→Blue T1 (keeping SimHub's
`blue && !green` suppression) · white→White T0 · green/greenHeld→Green T1 ·
startReady/oneLapToGreen→Ready, startSet/greenHeld→Set, startGo→Go ·
tenToGo/fiveToGo→CountdownLaps · incident count vs limit−margin→
IncidentWarning.

**LMU and F1 are not implemented.** Every mapping has to be checked against
real telemetry before it is trusted — prose-only rF2 claims have been wrong
here before — and that live-verification session has not happened. Until a
refiner is written and verified, every sim except iRacing runs on the generic
adapter.

## 11. Conformance strategy

**The renderer has no byte corpus, deliberately.** While the design is still
moving, byte-exactness is the wrong contract for the painters: pinning frames
makes every intended visual tweak a mass regeneration whose diff reads
`Binary files differ`. What covers them instead:

- **The scenario catalogue**
  (`plugin/core/Rendering/Grammar/ScenarioCatalogue.cs`) — ~33
  curated scripts covering the signal vocabulary, each a
  `{ name, description, script: [{frame, state}, …], sample_frame }` replayed
  from frame 0. The envelope makes rendering a function of state *history*, so
  the script, not a lone state, is the unit. Sample frames are chosen to
  discriminate: strobe phases, breathe peaks, sweep positions, flash blend
  weights, fade depths.
- **A smoke pass** (`GrammarSmokeTests`) replays every scenario and asserts
  only what survives visual tuning: no throw, a whole 3072-byte frame,
  deterministic output, a survivable window either side of the sample frame,
  and darkness exactly where darkness is the signal. It catches
  compositor/envelope interaction bugs the per-painter tests miss.
- **Per-painter unit tests** (`GrammarPainterTests`, `GrammarCompositorTests`,
  `GrammarEnvelopeTests`) assert specific pixels and slot/phase decisions.
- **Visual review is a tool, not a test.** `just frames-sheet` renders the
  whole catalogue to one labelled contact sheet; `just frames <scenario>`
  renders one frame to PNG.
- **The adapters** are covered by plain C# tests (`AdapterTests`,
  `IRacingAdapterTests`), including hand-built synthetic `SessionFlags`
  sequences.
- **The wire protocol keeps its golden vectors.** That contract is about
  bytes, so bytes are the right fixture; nothing here changes it.
