# Web overlay

The browser-rendered virtual panel: the SimHub plugin hosts a small
localhost-only web server, and any browser — or a DashStudio Web Page View
running as an in-game overlay — renders the 32×32 flag panel as an LED-dot
canvas. No hardware involved anywhere on this path.

## Architecture

| Piece | Where | What it does |
|---|---|---|
| `OverlayWebServer` | `plugin/core/Web/` | Raw `TcpListener` bound to **127.0.0.1:8972** (fixed port). `GET /` serves the overlay page; `GET /stream` returns a never-ending response whose body is frames. |
| Overlay page | `overlay/index.html` | 32×32 canvas with the LED-dot skin (round dots on a dark background), painted straight from the stream. Auto-reconnects with backoff; shows an on-page status line while disconnected. |
| Overlay dash | `overlay/dash/Uniflag Overlay/` | Ready-made DashStudio overlay: a 320×320 Web Page View pointed at `http://127.0.0.1:8972/?v=2` (a clean 10× upscale of the 32×32 panel, well inside the 800×600 overlay cap). |

Wire format on `/stream`: raw **3072-byte** RGB888 frames (32×32,
row-major, top-left origin) written back to back forever, no COBS/CRC and no
record framing — the server writes each frame whole or drops the connection,
so the reader just counts bytes. Frames go out at
**30 fps** by forwarding every even tick of the renderer's 60 fps clock
(parity decimation stays locked to the render clock and stays honest across
skipped ticks).

Design points that are contracts, not accidents:

- **Localhost binding is the security boundary.** The listener binds
  `IPAddress.Loopback`, never `0.0.0.0`. From another LAN machine the URL
  must be unreachable.
- **Renderer runs only while someone watches.** The server registers one
  frame sink with `RendererLoop` when the first stream client connects and
  unregisters it when the last one leaves. The server keeps reading from
  each client purely to notice it going away promptly — a closed tab must
  stop the render thread even when nothing is being painted.
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
- **The transport is plain HTTP, not a WebSocket.** The page only ever
  receives and the record size is fixed, so RFC 6455 framing would buy
  nothing. The cost is one `Access-Control-Allow-Origin: *` header on
  `/stream`: a fetch, unlike a WebSocket handshake, is subject to CORS, and
  `just overlay-serve` runs the page from a different port. Any page the user
  visits can therefore read the loopback frame stream — already true of the
  WebSocket endpoint, which never checked Origin.

## Using it in a browser

Open <http://127.0.0.1:8972/> on the SimHub machine. That's all. The page
reconnects automatically when SimHub restarts.

Page query parameters:

| Param | Meaning |
|---|---|
| `?v=N` | Cache-buster, ignored by the page (see below). |
| `?stream=URL` | Override the frame source (default `http://127.0.0.1:8972/stream`). |
| `?src=dummy` | Built-in 30 Hz test pattern instead of the stream — skin iteration with no plugin running, and the only way to see the page render off Windows (pairs with `just overlay-serve`). |

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
   `http://127.0.0.1:8972/?v=2` by itself.

**Cache-busting (`?v=`):** the Web Page View caches pages aggressively
*across plugin updates* — the server sends `Cache-Control: no-store` on
every response, but the embedded view has been observed to hold on to stale
pages anyway. The dash URL therefore carries an explicit `?v=` version
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
> motion smoothness suffers. (The licensed tier paints ~26–30 fps in the
> Web Page View — visually fine for a panel whose fastest effect is a 4 Hz
> strobe.) To diagnose a cap, open the page with `?src=dummy` and watch the
> white cursor pixel: it advances one LED per frame at 30 Hz, so a chunky,
> jumping cursor is a cap and a smooth one is not. Watching the pixel is the
> only reliable test — if the cap works by sampling the embedded browser's
> texture rather than by throttling the page, nothing measurable *inside*
> the page can see it at all.

## Page iteration outside SimHub

`just overlay-serve` serves the `overlay/` directory on a local port; the
page defaults its frame source to the plugin's
`http://127.0.0.1:8972/stream`, so a dev copy of the page runs against the
live plugin (this is what the CORS header on `/stream` is for). With
`?src=dummy` no plugin is needed at all, which is the only way to work on
the page off Windows. The embedded copy only updates on
`just plugin-build`, so in-plugin serving always matches the last build, not
the working tree.
