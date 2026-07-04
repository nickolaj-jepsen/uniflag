# uniflag effects specification

Executable-spec companion for the C# renderer port.

> **M11 note.** The `render/` crate was deleted at M11; every `render/…`
> file:line citation below refers to git history (the pre-M11 tree). The
> ported-parity golden corpus in `testdata/frames/` is **permanently frozen**
> — its dumper died with the crate, so it is unregenerable by design — and
> remains the arbiter. This spec is frozen with it.

**Authority order.** The Rust code in `render/src/effects.rs` + `render/src/anim.rs`
and the golden frames in `testdata/frames/` are the spec source; this document is a
faithful transcription of that code, written so the C# port can reproduce every
effect **byte-exactly** from this doc plus the golden corpus (section 7). Where this
document and the code disagree, the code and goldens win — file a doc fix. The
`firmware/src/runtime.rs` module doc-comment (lines 14–59) is authoritative **only**
for the precedence ladder and design intent; three of its per-flag parameter claims
are stale and are corrected in section 4.1 of this document.

All `file:line` references were traced at authoring time (branch `v2`, M3). If the
cited files move under later milestones, the golden corpus remains the arbiter.

---

## 1. Frame model

- Panel is **32 × 32** RGB pixels: `WIDTH = 32`, `HEIGHT = 32`
  (`render/src/lib.rs:33-35`). Colours are 8-bit-per-channel RGB triples
  (`render/src/surface.rs:10`).
- The renderer runs a **60 fps internal frame counter** (`u32`, wrapping). The
  firmware ticks it every `FRAME_TICK_MS = 16` ms (`firmware/src/runtime.rs:75`,
  increment at `runtime.rs:124-126` via `wrapping_add`). All animation math is
  written in **frames**; Hz figures in this document are the 60 fps conversions.
  The C# renderer must keep a 60 fps counter and let sinks sample it — rebasing
  the math to a sink rate (e.g. 30 fps) would halve every strobe rate
  (docs/v2-plan.md, "Renderer clock discipline").
- Entry point — the whole panel is recomputed from scratch every tick:

  ```
  paint(surface, state, frame, flag_age, connected)   // render/src/effects.rs:79
  ```

  - `state` — the flag/wave/session/caution/sectors tuple (`proto::State`).
  - `frame: u32` — the 60 fps tick counter.
  - `flag_age: u32` — frames since `state.flag` last **changed**. The firmware
    records `flag_changed_at` only when the incoming flag differs
    (`firmware/src/runtime.rs:117-120`) and computes
    `age = frame.wrapping_sub(flag_changed_at)` (`runtime.rs:133`). Session or
    caution changes do **not** reset it. Only red (onset flash), green (onset
    sweep) and the ready orb (5 s fallback) consume it.
  - `connected: bool` — false once no host update has arrived within
    `CONNECT_TIMEOUT = 1500 ms` (`firmware/src/runtime.rs:85, 134`).
- Paint-target contract (`render/src/surface.rs:15-21`): coordinates are `i32`,
  and **out-of-range `set_pixel` calls are silently ignored**. Effect arithmetic
  genuinely goes negative — the green onset sweep band centre starts at
  `x = -4` (section 5.4) — so the C# paint target must clamp, and all coordinate
  math must be signed. Reference implementations of the clamp:
  `render/tests/common/mod.rs:71-77` and `render/examples/dump_golden.rs:49-57`.
- `fill(color)` and `fill_with(f)` are plain double loops over
  `y in 0..32, x in 0..32` calling `set_pixel` (`render/src/surface.rs:24-45`).
  Iteration order never matters for the final frame (each pixel written once per
  layer), but the sector-band overlay (section 6) is painted **after** the base
  layer and overwrites it.

---

## 2. Animation primitives — bit-exact

All primitives live in `render/src/anim.rs` and are pure integer math (the RP2040
has no FPU). The C# port must use the same integer operations — no floats, no
rounding-mode surprises.

### 2.1 `SIN_U8` — 256-entry sine LUT (`render/src/anim.rs:8-26`)

8-bit unsigned sine indexed by phase `0..=255` (one full period), centred at 128,
generated at compile time by the **Bhaskara I** approximation. The normative
generation formula, transcribed exactly from `build_sin_lut` (`anim.rs:10-26`),
for each index `k` in `0..=255` (all `u32` arithmetic, integer division
truncates):

```
neg = k >= 128
a   = k % 128
q   = a * (128 - a)
mag = (16 * q * 127) / (81920 - 4 * q)      // integer division
SIN_U8[k] = neg ? saturating_sub(128, mag)  // clamps at 0; mag <= 127 so never hit
              : 128 + mag
```

Anchor values (derived from the formula; the golden frames pin the rest):

| k | SIN_U8[k] | note |
|---|-----------|------|
| 0   | 128 | period start, mid-level |
| 1   | 131 | |
| 63  | 254 | |
| 64  | **255** | peak (single sample) |
| 65  | 254 | |
| 128 | 128 | mid-level, descending |
| 192 | **1** | trough (single sample) — note: 1, not 0 |
| 255 | 125 | |

The C# port may either re-run the const formula or embed the 256 values verbatim;
either way the golden corpus verifies it transitively through every wave/breathe
effect.

### 2.2 `strobe_60(frame, hz) -> bool` (`render/src/anim.rs:30-34`)

On/off strobe with ~60 % duty, pinned to the 60 fps tick:

```
period = max(60 / hz, 1)          // integer division; hz is u8, >= 1
on     = (period * 6 + 5) / 10    // ~60 % duty, rounded — exact formula matters
return (frame % period) < on
```

Values for every rate actually used (matches the derivation table in
`render/src/scenarios.rs:24-37`):

| hz | period (frames) | on (frames) | duty | used by |
|----|-----------------|-------------|------|---------|
| 2 | 30 | 18 | 60 % | yellow/red/green single-wave, sector band (B=0/1) |
| 3 | 20 | 12 | 60 % | white single-wave |
| 4 | 15 | 9 | 60 % | red/green double-wave, sector band (B=2) |
| 5 | 12 | 7 | 58.3 % | white double-wave |

### 2.3 `breathe(frame, period_frames) -> u8` (`render/src/anim.rs:38-41`)

Sine envelope over `period_frames`:

```
p = (frame % period_frames) * 256 / period_frames   // integer division
return SIN_U8[p & 0xFF]
```

Actual output range is **1..=255** (LUT trough is 1, not 0), and the envelope
**starts at 128** at `frame % period == 0`, peaks at 255 one quarter-period in
(`p == 64`), passes 128 at half-period, and troughs at 1 three-quarters in
(`p == 192`). (The `anim.rs` doc-comment's "half-sine 0..=255" phrasing is
imprecise; this paragraph describes the real behaviour.) For period 240:
peak at frame 60, trough at frame 180 — both divisions exact
(`render/src/scenarios.rs:39-43`).

### 2.4 `wave_mult(x, y, frame, lo, hi) -> u8` (`render/src/anim.rs:45-54`)

Per-pixel diagonal "cloth wave" brightness multiplier:

```
phase = (16 * (x as u32)  +  8 * (y as u32)  +  4 * frame)  mod 256
        // implemented as u32 wrapping_mul / wrapping_add, then & 0xFF —
        // i.e. everything mod 2^32 in two's complement, low byte taken.
s     = SIN_U8[phase] as u16
span  = (hi - lo) as u16          // callers always satisfy hi >= lo
return lo + (s * span) / 255      // u16 math, integer division, cast to u8
```

C# port: compute the phase in `unchecked` `uint` arithmetic. The `x as u32` cast
is a two's-complement reinterpretation — negative `x` would wrap — but note that
every current call site passes in-range `0..32` coordinates (`fill_with` loops
and the sector band), so the cast never actually fires on a negative. Result
range is `[lo, hi]`: `s = 255` yields exactly `hi`, `s = 1` yields `lo` for all
used spans.

`(lo, hi)` pairs in use: `(150, 255)` yellow/red/blue static base,
`(220, 255)` green/white base, `(180, 255)` sector band active pulse.

### 2.5 `scale_rgb(c, m) -> Rgb` (`render/src/anim.rs:57-63`)

Multiply a colour by an 8-bit brightness multiplier:

```
m' = (m as u16) + 1                    // 1..=256
channel_out = ((channel as u16) * m') >> 8
```

`m = 255` reproduces the input exactly; `m = 0` yields `c >> 8` per channel
(i.e. 0 for all 8-bit inputs... exactly: `(c * 1) >> 8`, which is 0 for c ≤ 255).

### 2.6 Euclidean division/remainder sites — C# hazard list

Rust `div_euclid`/`rem_euclid` **floor**; C# `/` and `%` **truncate toward zero**
and `%` returns negative results for negative dividends. Every site, flagged:

| Site | Expression | Can the operand go negative? | C# porting note |
|------|-----------|------------------------------|-----------------|
| Blue sweep, `render/src/effects.rs:201` | `(x - sweep_pos).rem_euclid(32)` | **Yes** — `x < sweep_pos` is common | `((x - p) % 32 + 32) % 32`, or equivalent branch |
| Checkered, `render/src/effects.rs:294` | `(x + off).div_euclid(4)` and `(y + off).div_euclid(4)` | No in current geometry (`x, y >= 0`, `off = frame/8 >= 0`) | Port as floor-division anyway — do not silently substitute `/` and leave a latent trap |

Related signed-arithmetic sites (safe in C# but must stay signed `int`):
`(x - pos).abs() < BAND_HALF` in the green sweep (`effects.rs:220`, `pos` starts
at −4), `(x - y).abs() <= 1` / `(x + y - 31).abs() <= 1` in the black-flag X
(`effects.rs:365-366`), and the ready-orb half-pixel deltas `2x - 31`
(`effects.rs:341-342`).

Other integer-op hazards: the frame counter and all phase math are **wrapping
u32** (`uint` + `unchecked` in C#); `saturating_add` appears in the caution
border brightness (`effects.rs:393`) and `saturating_sub` in the LUT builder
(`anim.rs:19`); every division in this spec truncates.

---

## 3. Palette and shared constants (`render/src/effects.rs:12-32`)

| Constant | RGB | Line |
|----------|-----|------|
| `BLACK` | (0, 0, 0) | effects.rs:12 |
| `YELLOW` | (255, 220, 0) | effects.rs:14 |
| `BLUE` | (0, 64, 255) | effects.rs:15 |
| `RED` | (255, 0, 0) | effects.rs:16 |
| `GREEN` | (0, 220, 0) | effects.rs:17 |
| `WHITE` | (255, 255, 255) | effects.rs:18 |
| `ORANGE` | (255, 90, 0) | effects.rs:19 |
| `SECTOR_DIM` | (40, 30, 0) | effects.rs:21 |

Geometry constants: `SECTOR_BAND_HEIGHT = 2` (effects.rs:23),
`SECTOR_SEGMENTS = [(0, 9), (11, 20), (22, 31)]` inclusive column ranges
(effects.rs:26), `GLYPH_W = 7`, `GLYPH_H = 11`, `CAUTION_BORDER = 2`
(effects.rs:30-32).

---

## 4. Precedence ladder

Taken from the `runtime.rs` doc-comment (`firmware/src/runtime.rs:39-59`) —
which **is** authoritative for precedence — and matching the actual dispatch in
`paint` (`render/src/effects.rs:80-111`). Highest wins:

1. **Disconnected** (`connected == false`): the v1 code fills the panel black and
   returns — no overlays (`effects.rs:80-84`). (v2 replaces this with the
   firmware-fallback idle of section 7a.)
2. **Red flag** — beats caution and everything else (`effects.rs:88`).
3. **Caution**: `C=V` (VSC board) then `C=S` (SC board) — beats every flag except
   red (`effects.rs:89-90`). E.g. yellow flag + SC renders the SC board.
4. **Per-flag base layer** — yellow, blue, green, white, black, orange,
   checkered (`effects.rs:91-97`).
5. **Session idle** (`F=N`): `Racing`/`Paused` → race-idle alive marker; any
   other session → ready orb with 5 s fallback (`effects.rs:98-104`).

**Overlay, painted last:** the sector band renders on top of whatever the steps
above produced — **unless the flag is red**, which suppresses it
(`effects.rs:109-111`; rationale at `effects.rs:106-108`). The band does overlay
caution boards.

The precedence layer must be its own separately unit-tested component in the C#
port (docs/v2-plan.md M3 step 5).

### 4.1 Corrections to stale `runtime.rs` claims

The `runtime.rs` doc-comment's per-flag claims are stale in the three numeric
parameter claims called out by docs/v2-plan.md's standing rules, plus two
shape/behaviour mischaracterisations found while authoring this spec. **This
document supersedes them**; the values below are traced from the code:

| Claim in `firmware/src/runtime.rs` | Reality (`render/src/effects.rs`) |
|---|---|
| Blue: "0.5 Hz breathing under static; 2-3 Hz breathing + a brighter sweep band under wave levels" (runtime.rs:22-23) | Blue never breathes. Static: cloth-wave overlay (150..255). Waved: cloth-wave **plus** a wrapping 4-px bright band sweeping L→R at 1 px / 2 frames (single) or 1 px / frame (double) (effects.rs:185-208) |
| Orange: "period 1 s / 250 ms / ~133 ms under wave 0/1/2" (runtime.rs:30-31) | Step periods are 90 / 24 / 12 frames = **1.5 s / 400 ms / 200 ms** (effects.rs:260-264) |
| Checkered: "scrolling diagonally at 30 px/s" (runtime.rs:32) | 1 px / 8 frames = **7.5 px/s** (effects.rs:291-292) |
| Yellow B=2: "a 4 Hz strobe" (runtime.rs:18-19) | A 4 Hz **anti-diagonal split-triangle alternation** (the two halves alternate), not a whole-panel strobe — see §5.1 |
| Ready orb: "fades to the same minimal alive marker" (runtime.rs:36-37) | Hard switch at frame 300, no fade — see §6 |

---

## 5. Per-flag base layers

Common helper: `paint_with_wave(base, frame, lo, hi)` fills every pixel with
`scale_rgb(base, wave_mult(x, y, frame, lo, hi))` (`render/src/effects.rs:466-471`).

### 5.1 Yellow (`render/src/effects.rs:114-153`)

| Wave | Rendering |
|------|-----------|
| `B=0` | `paint_with_wave(YELLOW, frame, 150, 255)` — cloth wave ≈59–100 % brightness (effects.rs:118) |
| `B=1` | Whole-panel strobe: `strobe_60(frame, 2)` → fill `YELLOW`, else fill `BLACK` (effects.rs:120-127). 2 Hz, 18/30 duty |
| `B=2` | Anti-diagonal split-triangle alternation, **not** `strobe_60` (effects.rs:129-151): `PERIOD = 15` (4 Hz); `upper_on = (frame % 15) < 7` (`PERIOD/2 = 7`); pixel is in the upper-left triangle iff `x + y <= 31`; lit triangle gets `YELLOW`, other `BLACK`. Exactly one triangle lit at all times; the dividing anti-diagonal (`x + y == 31`) belongs to the upper triangle. Duty is asymmetric: upper lit 7 frames, lower 8, per 15-frame period |

### 5.2 Red (`render/src/effects.rs:155-183`)

1. **Onset flash**, regardless of wave level: `ONSET_FRAMES = 4` (≈66 ms). If
   `flag_age < 4`: `progress = (flag_age * 255 / 4) as u8`, `gb = 255 - progress`,
   fill `(255, gb, gb)` (effects.rs:159-165). Exact frames: age 0 →
   (255, 255, 255), 1 → (255, 192, 192), 2 → (255, 128, 128), 3 → (255, 64, 64).
2. Then: `B=1` → 2 Hz `strobe_60` full-fill `RED`/`BLACK`; `B=2` → 4 Hz same
   (effects.rs:166-180). No cloth wave while strobing.
3. `B=0` → `paint_with_wave(RED, frame, 150, 255)` (effects.rs:182).

### 5.3 Blue (`render/src/effects.rs:185-208`)

| Wave | `sweep_step_frames` | Rendering |
|------|--------------------|-----------|
| `B=0` | — | `paint_with_wave(BLUE, frame, 150, 255)` (effects.rs:194-196) |
| `B=1` | 2 | sweep at 1 px / 2 frames = 30 px/s; wraps every 64 frames ≈ 0.94 Hz |
| `B=2` | 1 | sweep at 1 px / frame = 60 px/s; wraps every 32 frames = 1.875 Hz |

Waved rendering (effects.rs:199-207): `sweep_pos = ((frame / sweep_step_frames)
% 32) as i32`; per pixel, if `(x - sweep_pos).rem_euclid(32) < 4` the multiplier
is 255 (bright band, 4 px wide, **wrapping** around the right edge back to the
left — this is the `rem_euclid` hazard site, section 2.6), otherwise
`wave_mult(x, y, frame, 150, 255)`; final colour `scale_rgb(BLUE, m)`.

### 5.4 Green (`render/src/effects.rs:210-241`)

1. **Onset sweep** for `flag_age < SWEEP_FRAMES = 30` (500 ms), ignoring wave
   level (effects.rs:213-228): `BAND_HALF = 4`, `span = 32 + 2*4 = 40`,
   `pos = (flag_age * 40 / 30) - 4` (signed integer math — **pos starts at −4**
   at age 0). Pixel is in-band iff `(x - pos).abs() < 4` — a **7-px-wide** band
   (`pos-3 ..= pos+3`). In-band colour `(200, 255, 200)`, otherwise `GREEN`.
   At age 0 the band is entirely off-panel (columns −7..−1): the frame is solid
   `GREEN`. The band's first visible column appears at age 1 (`pos = −3`), and
   its last sliver (column 31, `pos = 34`) shows at age 29.
2. Settled (`flag_age >= 30`): `B=1` → 2 Hz, `B=2` → 4 Hz via `strobe_60`;
   off-phase fills `BLACK`, on-phase (and `B=0`) renders
   `paint_with_wave(GREEN, frame, 220, 255)` (effects.rs:230-240). Note the
   on-phase keeps the cloth wave (unlike yellow/red strobes, which fill flat).

### 5.5 White (`render/src/effects.rs:243-254`)

`B=1` → 3 Hz, `B=2` → 5 Hz `strobe_60`; off-phase `BLACK`; on-phase and `B=0`
render `paint_with_wave(WHITE, frame, 220, 255)`.

### 5.6 Black flag (`render/src/effects.rs:352-373`)

Solid black with a pulsing white "X" across both diagonals:

- `PERIOD = 100` frames (0.6 Hz breathe, effects.rs:357).
- `m = (breathe(frame, 100) as u16 * 130 / 255) as u8` → range 0..=130
  (envelope trough 1 → 0, peak 255 → 130).
- X geometry (effects.rs:363-372): pixel lit iff `|x - y| <= 1` (main diagonal)
  or `|x + y - 31| <= 1` (anti-diagonal); lit colour `(m, m, m)`, else `BLACK`.
  Each diagonal is 3 px wide per row; both run corner to corner on the square
  panel.

### 5.7 Orange / meatball (`render/src/effects.rs:256-283`)

Rotating quartered black/orange pattern; two adjacent quadrants lit, rotating
clockwise:

- Step period: `B=0` → 90 frames (1.5 s), `B=1` → 24 (400 ms), `B=2` → 12
  (200 ms) (effects.rs:260-264). Full rotation = 4 steps = 6 s / 1.6 s / 0.8 s.
- `step = (frame / period) % 4` (effects.rs:265).
- Quadrants split at `half_w = half_h = 16`; numbered clockwise from top-left:
  `0 = TL (x<16, y<16)`, `1 = TR (x>=16, y<16)`, `2 = BR (x>=16, y>=16)`,
  `3 = BL (x<16, y>=16)` (effects.rs:268-275).
- Quadrant lit iff `q == step || q == (step + 1) % 4`; lit → `ORANGE`, else
  `BLACK` (effects.rs:276-281).

### 5.8 Checkered (`render/src/effects.rs:285-301`)

- `TILE = 4`; `off = (frame / 8) as i32` — the pattern advances 1 px per 8
  frames ≈ **7.5 px/s** diagonally (tile boundaries drift toward the top-left as
  `off` grows); the pattern maps onto itself every 4 off-increments = 32 frames
  ≈ 533 ms (one whole tile) (effects.rs:285-292).
- `cell = ((x + off).div_euclid(4) + (y + off).div_euclid(4)) & 1`;
  `cell == 0` → `BLACK`, else `WHITE` (effects.rs:293-300). `div_euclid` hazard
  site — section 2.6. At frame 0 the top-left 4×4 tile is black.

### 5.9 Caution boards — VSC and SC (`render/src/effects.rs:375-430`)

Shared board renderer `paint_caution_board` (effects.rs:390-418):

- **Border**: 2-px yellow frame on all four edges — pixel is border iff
  `x < 2 || x >= 30 || y < 2 || y >= 30`. Brightness breathes at
  `BORDER_PERIOD = 240` frames (0.25 Hz):
  `m = saturating_add(200, (breathe(frame, 240) as u16 * 55 / 255) as u8)` →
  range 200..=255 (≈78–100 %); border colour `scale_rgb(YELLOW, m)`
  (effects.rs:391-407). Interior fills `BLACK`.
- **Letters**: white (`WHITE`) 7×11 glyphs on one centred row.
  `total_w = n*7 + (n-1)*gap`; `x_left = (32 - total_w) / 2`;
  `y_top = (32 - 11) / 2 = 10`; glyph *i* at `ox = x_left + i * (7 + gap)`
  (effects.rs:409-417). Glyph bits: each row byte holds the glyph row in its low
  7 bits, **bit 6 = leftmost column**; only set bits are painted (background
  shows through) (effects.rs:28-31, 422-430).
- **VSC** (`C=V`): glyphs `[V, S, C]`, `gap = 1` → `total_w = 23`,
  `x_left = 4`; letters occupy x 4–10 / 12–18 / 20–26, y 10–20
  (effects.rs:375-379).
- **SC** (`C=S`): glyphs `[S, C]`, `gap = 4` → `total_w = 18`, `x_left = 7`;
  letters at x 7–13 / 18–24, y 10–20 (effects.rs:381-385).

Glyph bitmaps, exact (`render/src/effects.rs:34-77`), MSB(bit 6)=left:

```
GLYPH_S (effects.rs:35-47)   GLYPH_C (effects.rs:50-62)   GLYPH_V (effects.rs:65-77)
0b0111110  .#####.           0b0111110  .#####.           0b1100011  ##...##
0b1100011  ##...##           0b1100011  ##...##           0b1100011  ##...##
0b1100000  ##.....           0b1100000  ##.....           0b1100011  ##...##
0b1100000  ##.....           0b1100000  ##.....           0b1100011  ##...##
0b0111110  .#####.           0b1100000  ##.....           0b0110110  .##.##.
0b0000011  .....##           0b1100000  ##.....           0b0110110  .##.##.
0b0000011  .....##           0b1100000  ##.....           0b0110110  .##.##.
0b0000011  .....##           0b1100000  ##.....           0b0011100  ..###..
0b1100011  ##...##           0b1100000  ##.....           0b0011100  ..###..
0b1100011  ##...##           0b1100011  ##...##           0b0011100  ..###..
0b0111110  .#####.           0b0111110  .#####.           0b0001000  ...#...
```

### 5.10 Session idle — race-idle marker (`render/src/effects.rs:303-319`)

`F=N` with session `Racing` or `Paused` (dispatch at effects.rs:98-102):

- Fill `BLACK`; `STATIC_M = 8` (effects.rs:309).
- Three static dim dots: `(0, 0)`, `(31, 0)`, `(0, 31)` at colour `(8, 8, 8)`
  (effects.rs:315-317).
- Bottom-right `(31, 31)` breathes: `PERIOD = 240` (0.25 Hz);
  `pulse_m = 4 + (breathe(frame, 240) as u16 * 10 / 255) as u8` → range 4..=14;
  colour `(pulse_m, pulse_m, pulse_m)` (effects.rs:310-318). Peak at frame 60,
  trough at frame 180 within each period (section 2.3).

### 5.11 Session idle — ready orb + 300-frame fallback (`render/src/effects.rs:321-350`)

`F=N` with any other session (`PreRace`, `PostRace`, `Replay`, `Unknown`):

- **Fallback**: if `flag_age >= ORB_DURATION_FRAMES = 300` (5 s), render the
  race-idle marker of 5.10 instead (effects.rs:327-331). (`flag_age` here is
  frames since the flag last changed — the orb window restarts on any flag
  transition into `None`, not on session changes.)
- **Ring**: `PERIOD = 120` (0.5 Hz breathe);
  `m = 40 + (breathe(frame, 120) as u16 * 160 / 255) as u8` → range 40..=200
  (effects.rs:332-335). Peak at frame 30 within each period.
- **Geometry** (effects.rs:336-349): centre is between pixels at (15.5, 15.5);
  work in half-pixel units `dx2 = 2x - 31`, `dy2 = 2y - 31`,
  `d_sq = dx2² + dy2²`; pixel lit iff `64 <= d_sq <= 144` (inclusive) — a
  hollow ring of real-pixel radius 4.0..6.0. Lit colour `(0, m, 0)`, else
  `BLACK`.

### 5.12 Rate summary (frames ↔ Hz at 60 fps)

| Effect | Frames | Hz / duration |
|---|---|---|
| Yellow/red/green single-wave strobe | period 30, on 18 | 2 Hz |
| Red/green double-wave strobe, sector band B=2 | period 15, on 9 | 4 Hz |
| Yellow double-wave triangle alternation | period 15, upper 7 / lower 8 | 4 Hz |
| White single / double strobe | 20 on 12 / 12 on 7 | 3 Hz / 5 Hz |
| Red onset flash | 4 | ≈66 ms |
| Green onset sweep | 30 | 500 ms |
| Blue sweep step (B=1 / B=2) | 2 / 1 per px | 30 / 60 px/s |
| Orange quadrant step (B=0/1/2) | 90 / 24 / 12 | 1.5 s / 400 ms / 200 ms |
| Checkered scroll | 8 per px | 7.5 px/s |
| Black-flag X breathe | 100 | 0.6 Hz |
| Caution border breathe | 240 | 0.25 Hz |
| Race-idle pulse | 240 | 0.25 Hz |
| Ready orb breathe / duration | 120 / 300 | 0.5 Hz / 5 s |
| Sector band pulse (B=0,1 / B=2) | 30 / 15 | 2 Hz / 4 Hz |

---

## 6. Sector band overlay (`render/src/effects.rs:432-464`)

Painted last, over any base layer except red (section 4), whenever
`state.sectors` is non-empty.

- **Geometry**: bottom `SECTOR_BAND_HEIGHT = 2` rows — y = 30 and 31
  (`y_start = 32 - 2`, effects.rs:444). Three segments with inclusive column
  ranges **S1 x = 0..=9, S2 x = 11..=20, S3 x = 22..=31**
  (`SECTOR_SEGMENTS`, effects.rs:26). The 1-px gap columns **10 and 21 are not
  written** — they keep whatever the base layer drew (effects.rs:433-435).
- **Pulse rate**: `strobe_hz = 4` if `wave == Double`, else `2` — note `B=1`
  uses the same 2 Hz as `B=0` (effects.rs:438-442).
- **Active sector** (its bit set in the `Z` mask): on-phase →
  `scale_rgb(YELLOW, wave_mult(x, y, frame, 180, 255))` per pixel; off-phase →
  `BLACK` (painted black, i.e. it overwrites the base layer)
  (effects.rs:450-456).
- **Inactive sector** (any other segment, while at least one sector is set):
  constant `SECTOR_DIM = (40, 30, 0)` (effects.rs:457-458) — so the whole band
  is always visible when any sector is flagged.

---

## 7. Three-state idle specification (M3-authored design)

> **Status: design spec, authored at M3.** This section is the single spec that
> both later milestones implement against: **M4** implements state (b) in the
> plugin renderer, **M8** implements state (a) in firmware (`screens.rs`).
> State (c) is existing coded behaviour, already covered by the golden corpus.
> States (a) and (b) are **PROPOSED, pending maintainer approval** — they are
> specified to the pixel so approval is a yes/no; on approval, delete the
> PROPOSED markers; on rejection, replace the subsection, not the structure.
>
> Rationale for three distinct looks: the user at the rig must be able to tell
> apart, at a glance, (a) "the plugin isn't running / USB is dead",
> (b) "the plugin is alive but no game is running", and (c) "the game is live,
> nothing is flagged". In v1 state (a) was a fully blank panel
> (`render/src/effects.rs:80-84`), indistinguishable from a powered-off device.

### 7a. Firmware fallback — device powered, no host stream (**PROPOSED**)

- **Trigger**: no valid host traffic for ~1.5 s, carrying over the v1 constant
  `CONNECT_TIMEOUT = 1500 ms` (`firmware/src/runtime.rs:85`; v2 contract:
  docs/v2-plan.md M6 step 2 "~1.5 s silence→fallback", M8 step 3c). Also the
  boot state before the first frame arrives.
- **Rendering** (60 fps frame counter `f`, free-running in the firmware):
  - Every pixel `BLACK` except pixel **(0, 0)** (top-left corner).
  - Pixel (0, 0) shows a **heartbeat blink**: colour **(40, 14, 0)** (dim amber)
    when `f % 120 < 6`, else black — i.e. **lit 100 ms every 2.0 s**.
  - The colour is derived once as `scale_rgb(ORANGE (255,90,0), 40)` =
    (40, 14, 0), but M8 must embed the **literal RGB** — the firmware no longer
    links the render crate (docs/v2-plan.md M8 steps 4, 6).
- **Distinctness**: single amber dot in the top-left, sharp blink, 2 s period —
  vs (b)'s continuous blue breathe at bottom-centre and (c)'s four grey corner
  dots / green ring. Race-idle also lights (0, 0), but statically grey
  `(8, 8, 8)` alongside three other corners; the fallback lights **only** this
  pixel, in amber, blinking.

### 7b. Plugin connected-idle — stream alive, no game (**PROPOSED**)

- **Trigger**: the plugin renderer is running (any sink active, frames being
  produced/streamed) and the adapter reports no game session. Implemented
  host-side at M4 step 3; the device shows it because the plugin streams it.
- **Rendering** (renderer frame counter `f`):
  - Every pixel `BLACK` except pixels **(15, 31)** and **(16, 31)**
    (bottom-centre pair; the sector band rows are free — no game means no
    sectors).
  - Both pixels: `m = 8 + (breathe(f, 240) as u16 * 16 / 255) as u8`
    (range 8..=24, 0.25 Hz), colour `scale_rgb(BLUE (0,64,255), m)` — i.e.
    from (0, 2, 8) at the trough to (0, 6, 24) at the peak, using the
    primitives of section 2 bit-exactly.
- **Distinctness**: blue vs amber/grey/green; bottom-centre pair vs corners;
  smooth breathe vs blink.

### 7c. Game live, no flag — as coded (normative today)

Exactly the existing session-idle renderings, already golden-pinned:

- Session `Racing`/`Paused` → **race-idle marker** of section 5.10
  (`render/src/effects.rs:303-319`; goldens `snap_race_idle`,
  `race_idle_breathe_peak`, `race_idle_breathe_trough`).
- Any other session → **ready orb** with the 300-frame fallback of section 5.11
  (`render/src/effects.rs:321-350`; goldens `snap_ready_orb`,
  `ready_orb_fallback_after_300`).

### Distinctness summary

| State | Pixels lit | Colour | Motion | Period |
|---|---|---|---|---|
| (a) firmware fallback | (0,0) only | amber (40,14,0) | blink 6 frames on | 120 frames / 2 s |
| (b) plugin connected-idle | (15,31), (16,31) | dim blue (0,2..6,8..24) | breathe | 240 frames / 4 s |
| (c1) race idle | 4 corners | grey (8,8,8) + pulse 4..14 | one corner breathes | 240 frames / 4 s |
| (c2) ready orb | ring r=4..6 centred | green (0,40..200,0) | breathe | 120 frames / 2 s |

---

## 8. Conformance — the executable spec

The prose above explains; these artifacts **decide**:

1. **Golden frame corpus — `testdata/frames/`.** One raw RGB888 dump per pinned
   scenario plus `manifest.json`. Produced exclusively by the dumper
   (`render/examples/dump_golden.rs`, run via
   `cargo run -p uniflag-render --example dump_golden`, wrapped by the
   `just golden-regen` recipe introduced by M3 step 2 — regeneration only in
   deliberate, reviewed commits, never as a test side effect).
   - **Frame format** (`dump_golden.rs:31-58`): exactly `32 * 32 * 3 = 3072`
     bytes; row-major; pixel `(x, y)` at byte offset `(y * 32 + x) * 3`; channel
     order R, G, B.
   - **Manifest format** (`dump_golden.rs:7-12, 137-171`): a JSON array, in
     scenario-table order, of
     `{ name, file, state: { flag, wave, session, caution, sectors }, frame,
     flag_age, connected }`; enum values are the PascalCase Rust variant names
     (`"Yellow"`, `"Single"`, `"PreRace"`, `"VirtualSafetyCar"`, …); `sectors`
     is an array of ints 1..=3.
   - **Ledger**: the scenario table `render/src/scenarios.rs:124-355`
     (currently 40 entries: the 19 legacy snapshot tuples + the 21 M3
     backfills). Names are unique, `lowercase_snake`, append-only
     (`scenarios.rs:15-17`; enforced at `dump_golden.rs:118-132` and
     `render/tests/golden_scenarios.rs:26-33`). The scenario-choice rationale
     (which frames discriminate which strobe rates, breathe extremes, etc.) is
     documented in the table's module comment (`scenarios.rs:19-66`).
2. **Comparison rule for the C# port** (v2-plan M3 step 8): iterate
   `manifest.json`; for each entry, construct the state, call the ported
   `paint(state, frame, flag_age, connected)` into a 3072-byte RGB888 buffer
   (same layout as above, out-of-range writes ignored), and compare **all 3072
   bytes for equality** against the `.rgb` file. No tolerance, no per-channel
   epsilon, no "close enough". Neither side ever generates its own fixtures
   (docs/v2-plan.md, "The cross-language contract").
3. **Rust snapshot suite** — the ASCII-art insta snapshots in
   `render/tests/snapshots/` (hand-written suite in `render/tests/effects.rs`,
   table-driven suite in `render/tests/golden_scenarios.rs` over the same
   scenario table). These are the Rust-side regression net and are
   human-reviewable, but the ASCII quantiser (`render/tests/common/mod.rs:79-104`)
   is deliberately lossy: known blind spots are the blue double-wave band
   position (`scenarios.rs:221-224`) and the race-idle breathe extremes
   (`scenarios.rs:332-336`), which only the raw corpus pins. **Where ASCII
   snapshots and raw frames could ever disagree, the raw frames win.**
4. **Determinism**: `paint` is a pure function of
   `(state, frame, flag_age, connected)` — no wall clock, no randomness
   (`dump_golden.rs:14-18`; risk register #8).
5. **Freeze**: the ported-parity corpus is regenerable only until **M11**; after
   the render crate is deleted the corpus is **permanently frozen** and becomes
   the sole ground truth (docs/v2-plan.md M11 step 1, cross-cutting policies).
   Later C#-authored effects (M10 penalty suite) get their own clearly separated
   corpus and never touch these files.
