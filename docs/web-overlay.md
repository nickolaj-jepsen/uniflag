# Web overlay — M2a feasibility spike

Measurements for the browser-rendered virtual panel (see `docs/v2-plan.md`,
milestone M2a). The test page is `overlay/index.html`: a 32×32 LED-dot canvas
fed either by a local 30 Hz dummy generator (default) or by raw 3072-byte
RGB888 binary frames over a WebSocket. All numbers in the matrix below come
from the page's own instrumentation panel — record them, don't estimate.

## Running the page

- **file://** — open `overlay/index.html` directly in a browser. No server
  needed; the page is fully self-contained (no CDN, no external fonts).
- **http://** — `just overlay-serve` serves the `overlay/` directory on a
  local port, for contexts that refuse `file://` pages.
- **WebSocket source** — append `?ws=<url>` (e.g.
  `?ws=ws://127.0.0.1:8972/ws` — the plugin's spike WS host) to replace
  the dummy generator with live
  frames. Each binary message must be exactly 3072 bytes (32×32 RGB888,
  row-major, top-left origin, no COBS/CRC on WS); wrong-size messages are
  ignored but counted. The page auto-reconnects with backoff.

### Reading the panel

- `rAF /s` — `requestAnimationFrame` callbacks per second: is the render loop
  itself throttled?
- `recv /s` — frames delivered per second (generator ticks or WS messages).
- `paint /s` — frames actually drawn. Latest-frame-wins, so `recv − paint`
  accumulates in `dropped` (coalesced, never an error by itself).
- `badsize` — WS messages that were not exactly 3072 binary bytes.
- `gap ms` — max interval between consecutive deliveries (last window /
  session max): catches stalls and bursts that averages hide.

The distinction is the whole point of M2a: a 10 fps dash cap that throttles
the embedded browser's rAF but not WS delivery reads as `rAF ≈ 10`,
`recv ≈ 30`, `paint ≈ 10`, with `dropped` climbing ~20/s.

Two caveats when reading the numbers:

- **In-page counters cannot detect a capture-side cap.** If SimHub's cap
  works by sampling the embedded browser's texture at 10 fps (rather than
  throttling rAF inside the page), every HUD number reads perfect while the
  visible overlay updates at 10 fps. After recording HUD numbers, **eyeball
  the white cursor pixel**: it advances one LED per frame at 30 Hz, so
  smooth-HUD-but-chunky-cursor means the cap is capture-side. Record that
  observation in the "cursor motion" column — the free-tier row must never
  be filled from HUD numbers alone.
- **In dummy mode, `recv /s` tracks the nominal generator timeline** even
  when timer throttling delivers ticks in bursts (catch-up counts skipped
  indices as received). Judge delivery smoothness from `gap ms`, not
  `recv /s`.

## Test matrix

Result cells are filled from the dev box only — no synthetic numbers.

Measured 2026-07-03 on the dev box (SimHub 9.11.21, licensed; plugin spike
WS host on ws://127.0.0.1:8972/ws).

| Environment | rAF fps | received /s | painted /s | cursor motion (eyeball) | verdict |
|---|---|---|---|---|---|
| Desktop Chrome / Edge (baseline) | 120 | 30 | 30 | good | good |
| DashStudio Web Page View — licensed tier | 30 | 30 | 26 | a bit jittery | good enough |
| DashStudio Web Page View — free tier (10 fps dash cap behaviour) | — | — | — | — | *not measurable on a licensed install; see below* |
| SimHub HTML rendering mode (issue #1494) | — | — | — | — | *not reproduced locally; known broken upstream, see below* |

Notes on the measured rows: the desktop baseline is ideal (paint locks to
the 30 Hz delivery; the 144 Hz-class display gives rAF 120). The licensed
Web Page View runs its embedded browser at 30 fps rAF and paints ~26/s —
occasional coalesced frames and slightly jittery cursor motion, visually
acceptable for a flag panel whose effects are ≤ 5 Hz strobes.

## Verdict

**GO** — the Web Page View overlay is viable on the licensed tier.

- **Overlay target fps: 30** (matches the WS delivery rate; the licensed
  Web Page View sustains ~26 painted with acceptable jitter — no design
  change warranted, flag effects are ≤ 5 Hz).
- **Free tier: untested** (there is no way to simulate the free tier on a
  licensed install; unlicensing the dev box wasn't worth it). The 10 fps
  dash cap *may* throttle the overlay. Documented degradation, not a
  redesign risk: even at a capture-side 10 fps the panel remains readable —
  flag changes and ≤ 5 Hz strobes survive; only motion smoothness suffers.
  Revisit if a free-tier user reports choppiness (the HUD + cursor
  procedure above diagnoses it in one look).
- **HTML rendering mode: do not enable.** Not reproduced locally (the
  setting was not locatable on 9.11.21), but Web Page View is reported
  broken under it upstream (SimHub issue #1494). User docs must state:
  the uniflag overlay requires the default rendering mode.
- Carry-forward for M5: cache-bust the page URL with a version query param
  (Web Page View caches across plugin updates); localhost-only binding
  stands.
