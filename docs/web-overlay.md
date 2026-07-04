# Web overlay

The browser-rendered virtual panel: the SimHub plugin hosts a small
localhost-only web server, and any browser — or a DashStudio Web Page View
running as an in-game overlay — renders the 32×32 flag panel as an LED-dot
canvas. No hardware involved anywhere on this path.

## Architecture

| Piece | Where | What it does |
|---|---|---|
| `OverlayWebServer` | `plugin/src/Web/` | Raw `TcpListener` bound to **127.0.0.1:8972** (fixed port). `GET /` serves the overlay page; `GET /ws` upgrades to a WebSocket. |
| Overlay page | `overlay/index.html` | 32×32 canvas with the LED-dot skin (round dots on a dark background), painted straight from each binary WS message. Auto-reconnects with backoff; shows an on-page status line while disconnected. |
| Overlay dash | `overlay/dash/Uniflag Overlay/` | Ready-made DashStudio overlay: a 320×320 Web Page View pointed at `http://127.0.0.1:8972/?v=1` (a clean 10× upscale of the 32×32 panel, well inside the 800×600 overlay cap). |

Wire format on `/ws`: raw **3072-byte** RGB888 frames (32×32, row-major,
top-left origin), one binary message per frame, no COBS/CRC. Frames are
broadcast at **30 fps** — the M2a-measured overlay target — by forwarding
every even tick of the renderer's 60 fps clock (parity decimation stays
locked to the render clock and stays honest across skipped ticks).

Design points that are contracts, not accidents:

- **Localhost binding is the security boundary.** The listener binds
  `IPAddress.Loopback`, never `0.0.0.0`. From another LAN machine the URL
  must be unreachable.
- **Renderer runs only while someone watches.** The server registers one
  frame sink with `RendererLoop` when the first WS client connects and
  unregisters it when the last one leaves.
- **Slow clients cannot stall anything.** Each client has a depth-one
  newest-frame-wins outbound queue (stale frames are dropped — a flag panel
  only cares about the latest), and a socket that refuses progress for 2 s
  is disconnected. The render thread never blocks on a socket.
- **Bind failure is a status, never a crash.** If port 8972 is taken, the
  plugin keeps working (renderer, preview, everything); the failure text
  shows in the settings tab's *Web overlay* status area.
- **Single-source page.** `overlay/index.html` is embedded into the plugin
  assembly at build time (`Uniflag.Web.index.html`); `just overlay-serve`
  serves the identical file from disk for page iteration outside SimHub.
- The server is a raw `TcpListener` with hand-rolled HTTP/RFC 6455 rather
  than `HttpListener`: http.sys URL ACLs can demand elevation, and the test
  suite must run on an unprivileged CI runner.

## Using it in a browser

Open <http://127.0.0.1:8972/> on the SimHub machine. That's all. The page
reconnects automatically when SimHub restarts.

Page query parameters:

| Param | Meaning |
|---|---|
| `?v=N` | Cache-buster, ignored by the page (see below). |
| `?ws=URL` | Override the frame source (default `ws://127.0.0.1:8972/ws`). |
| `?hud=1` | Show the fps instrumentation panel (the M2a measurement HUD). |
| `?src=dummy` | Built-in 30 Hz test pattern instead of the WebSocket — skin iteration with no plugin running (pairs with `just overlay-serve` or plain `file://`). |

## Installing the overlay dash

The release zip ships the dash as `Uniflag Overlay.simhubdash` — a single
packaged file SimHub imports on double-click. It is produced from
`overlay/dash/Uniflag Overlay/` at release time by
[`packaging/make-simhubdash.ps1`](../packaging/make-simhubdash.ps1) (a ZIP
whose entries are folder-name-prefixed with backslash separators, matching
SimHub's own *Export Dashboard*); `just simhubdash` regenerates it locally.

1. With SimHub running, double-click `Uniflag Overlay.simhubdash` and
   confirm the import prompt — no admin file-copy needed. (Working from
   source instead? Copy the `overlay/dash/Uniflag Overlay/` folder into
   `C:\Program Files (x86)\SimHub\DashTemplates\` with SimHub closed.)
2. *Dash Studio* → the "Uniflag Overlay" dash appears in the dashboard
   list. Use *overlay* mode (e.g. add it in the *Overlays* / in-game
   overlay layout for your game) — it is flagged as an overlay and sized
   320×320.
3. The dash needs the Uniflag plugin enabled; the panel connects to
   `http://127.0.0.1:8972/?v=1` by itself.

**Cache-busting (`?v=`):** the Web Page View caches pages aggressively
*across plugin updates* — the server sends `Cache-Control: no-store` on
every response, but the embedded view has been observed to hold on to stale
pages anyway. The dash URL therefore carries an explicit `?v=1` version
query param. **When a plugin release changes `overlay/index.html`, bump the
`?v=` number in the dash** (edit the Web Page View URL in Dash Studio, or
re-import the updated committed dash).

## Warnings

> **Do not enable SimHub's HTML rendering mode.** The Web Page View is
> reported broken under it upstream (SimHub issue #1494). The uniflag
> overlay requires the default rendering mode. If the overlay shows a blank
> box while the browser URL works, check this first.

> **SimHub free tier caps dashes at 10 fps.** The overlay degrades but
> stays usable: flag changes and the ≤ 5 Hz strobe effects survive; only
> motion smoothness suffers. The licensed tier was measured at ~26–30
> painted fps (table below). To diagnose a cap, open the dash page with
> `?hud=1` and follow "Reading the HUD" below.

## Page iteration outside SimHub

`just overlay-serve` serves the `overlay/` directory on a local port for
contexts that refuse `file://`; the page defaults its frame source to the
plugin's `ws://127.0.0.1:8972/ws`, so a dev copy of the page runs against
the live plugin. With `?src=dummy` no plugin is needed at all. The embedded
copy only updates on `just plugin-build`, so in-plugin serving always
matches the last build, not the working tree.

## Evidence base: the M2a measurements

Kept verbatim as the justification for the 30 fps target and the dash
verdict. Measured 2026-07-03 on the dev box (SimHub 9.11.21, licensed;
M2a spike WS host on `ws://127.0.0.1:8972/ws`).

| Environment | rAF fps | received /s | painted /s | cursor motion (eyeball) | verdict |
|---|---|---|---|---|---|
| Desktop Chrome / Edge (baseline) | 120 | 30 | 30 | good | good |
| DashStudio Web Page View — licensed tier | 30 | 30 | 26 | a bit jittery | good enough |
| DashStudio Web Page View — free tier (10 fps dash cap behaviour) | — | — | — | — | *not measurable on a licensed install* |
| SimHub HTML rendering mode (issue #1494) | — | — | — | — | *not reproduced locally; known broken upstream* |

The desktop baseline is ideal (paint locks to the 30 Hz delivery; the
144 Hz-class display gives rAF 120). The licensed Web Page View runs its
embedded browser at 30 fps rAF and paints ~26/s — occasional coalesced
frames and slightly jittery cursor motion, visually acceptable for a flag
panel whose effects are ≤ 5 Hz strobes. The free tier could not be measured
on a licensed install; its 10 fps cap is a documented degradation, not a
redesign risk.

### Reading the HUD (`?hud=1`)

- `rAF /s` — `requestAnimationFrame` callbacks per second: is the render
  loop itself throttled?
- `recv /s` — frames delivered per second (WS messages, or generator ticks
  in `?src=dummy` mode).
- `paint /s` — frames actually drawn. Latest-frame-wins, so `recv − paint`
  accumulates in `dropped` (coalesced, never an error by itself).
- `badsize` — WS messages that were not exactly 3072 binary bytes.
- `gap ms` — max interval between consecutive deliveries (last window /
  session max): catches stalls and bursts that averages hide.

A 10 fps dash cap that throttles the embedded browser's rAF but not WS
delivery reads as `rAF ≈ 10`, `recv ≈ 30`, `paint ≈ 10`, with `dropped`
climbing ~20/s.

Two caveats when reading the numbers:

- **In-page counters cannot detect a capture-side cap.** If SimHub's cap
  works by sampling the embedded browser's texture at 10 fps (rather than
  throttling rAF inside the page), every HUD number reads perfect while the
  visible overlay updates at 10 fps. After recording HUD numbers, eyeball
  the motion (in `?src=dummy` mode, the white cursor pixel advances one LED
  per frame at 30 Hz): smooth-HUD-but-chunky-cursor means the cap is
  capture-side.
- **In dummy mode, `recv /s` tracks the nominal generator timeline** even
  when timer throttling delivers ticks in bursts (catch-up counts skipped
  indices as received). Judge delivery smoothness from `gap ms`, not
  `recv /s`.
