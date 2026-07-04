# SimHub setup

uniflag v2 ships as a native SimHub plugin. The plugin owns all state and
rendering — it reads the game's flag telemetry, renders 32×32 frames at
60 fps, and streams them to the panel over USB. The panel is a dumb
framebuffer device; there is nothing to configure on it.

> Tested with SimHub **9.11.21** (the project's pinned reference
> version). Any modern 9.x should work.

## Install the plugin

1. Copy `UniflagPlugin.dll` into the SimHub install directory
   (`C:\Program Files (x86)\SimHub` by default) — the same folder that
   holds `SimHubWPF.exe`. Do **not** put it in a subfolder; SimHub only
   scans its own directory.
2. Start SimHub. It shows a *"New plugins have been detected!"* dialog —
   enable **Uniflag** there (or later via **Settings → Plugins**).
3. A **Uniflag** entry appears in the left-hand menu. That tab holds the
   device status block, the brightness slider, the manual COM-port
   override, a live preview of the rendered panel, and the web-overlay
   server status.

Building the DLL yourself: `just plugin-build` (needs a SimHub install
for the reference assemblies — override the location with
`$env:UNIFLAG_SIMHUB_DIR`).

## Plug in the panel

Flash the firmware first (see the top-level README, `just flash`). Then
just plug the panel in — no port picking needed:

- **Auto-discovery**: the plugin scans for the panel's USB identity
  (VID `0x1209`, PID `0x0001` — pid.codes test PID; the registered PID
  `0xF1A6` is also accepted). It opens the port, performs the
  Hello/HelloAck handshake, and starts streaming frames at 30 fps.
- **Status**: the Uniflag tab shows the connection state (*Scanning* →
  *Connecting* → *Streaming*), the port, and the firmware version,
  protocol version, and panel size reported by the device.
- **Manual override**: if discovery can't see the port (unusual cabling,
  odd USB stack), enter a COM port in the tab's override field. This
  bypasses the VID/PID filter but the handshake still has to succeed —
  the plugin never streams to an unverified device.

## Brightness

- **Slider** in the Uniflag tab.
- **Device buttons**: short-press the panel's brightness-up /
  brightness-down buttons to step, and the third button to toggle sleep
  (panel near-off, wakes back to the remembered level). Button presses
  are sent to the plugin, which owns the policy — the value in the
  slider and the panel always agree.
- The setting persists in SimHub's plugin settings (never on the
  device) and is re-applied on every reconnect.
- **Long-press** any panel button to toggle the firmware's local test
  screen (corner markers, R/G/B bars, firmware version) — useful for
  checking the panel without SimHub.

## Web overlay + dash

The plugin serves a browser-rendered virtual panel at
`http://127.0.0.1:8972/` (localhost-only, by design). A ready-made
DashStudio overlay lives at `overlay/dash/Uniflag Overlay/` — import it
to get the virtual panel as an in-game overlay. Details, wire format,
and design contracts: [`docs/web-overlay.md`](../docs/web-overlay.md).

## Troubleshooting

- **Status stuck at *Scanning* — no device found**: check Device Manager
  for a COM port with VID `1209` / PID `0001` (or `F1A6`). If the OS
  doesn't see it, the plugin can't either — recheck cable and flashing.
  If the port exists but the plugin won't take it, another program may
  be holding it open (a serial terminal, or a leftover v1 *Custom Serial
  Devices* profile still bound to the port — delete that profile, it is
  obsolete in v2). The tab's last-error text names the failure; the
  manual override forces a specific port.
- **Panel dark except a brief amber blink in the top-left corner**:
  that's the firmware's fallback screen — the device is powered and
  healthy but receiving no frames. SimHub isn't running, the plugin
  isn't enabled, or the connection isn't in *Streaming*. Check the
  Uniflag tab.
- **Status shows *Refused* with a protocol-version (or panel-size)
  message**: plugin and firmware are from different releases. The plugin
  refuses to drive a mismatched device rather than failing silently —
  reflash the UF2 that shipped in the same release zip as the DLL.
- **Panel lit but shows a dim blue breathing dot at the bottom
  centre**: that's the plugin's connected-idle marker — the stream is
  alive but no game session is delivering telemetry. Start the sim (a
  game in menus/replay counts as no live session).
- **Wrong flag / no flag while racing**: the settings-tab preview, the
  web overlay, and the panel all render the same frames — compare them.
  If the preview matches the panel, the rendering path is fine and the
  problem is game-side mapping (see
  [`docs/simhub-flag-properties.md`](../docs/simhub-flag-properties.md)
  for what each sim exposes; some flags simply don't exist in some
  sims' telemetry).
- **Isolating hardware from SimHub**: close SimHub (it holds the COM
  port), then run `just cli` — `uniflag-cli` speaks the same protocol
  and streams a test pattern. If the panel responds, the hardware and
  firmware are good and the issue is on the SimHub side.
