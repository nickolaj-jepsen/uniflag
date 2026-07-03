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

| Environment | rAF fps | received /s | painted /s | cursor motion (eyeball) | verdict |
|---|---|---|---|---|---|
| Desktop Chrome / Edge (baseline) | | | | | |
| DashStudio Web Page View — licensed tier | | | | | |
| DashStudio Web Page View — free tier (10 fps dash cap behaviour) | | | | | |
| SimHub HTML rendering mode (issue #1494) | | | | | |

## Verdict

_TBD — after the matrix is measured: go / degraded call, the chosen overlay
target fps, and any required workarounds (cache-busting, rendering-mode
incompatibility notes, free-tier degradation)._
