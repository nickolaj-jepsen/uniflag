# uniflag — install

Everything in this zip belongs to one release and is versioned together.
Always install the plugin and firmware from the same zip — releases are
only tested as a matched pair, and the plugin refuses to drive a panel
whose protocol version differs.

| File | What it is |
|------|------------|
| `UniflagPlugin.dll` | SimHub plugin — reads flag telemetry, renders the frames, drives the panel |
| `uniflag.uf2` | Firmware for the Pimoroni Cosmic Unicorn (original RP2040 / Pico W only) |
| `Uniflag Overlay/` | DashStudio dash — the virtual panel as an in-game overlay |
| `INSTALL.md` | This file |

## 1. Plugin

1. Close SimHub.
2. Copy `UniflagPlugin.dll` into the SimHub install folder
   (`C:\Program Files (x86)\SimHub` by default — the folder containing
   `SimHubWPF.exe`). Not a subfolder; SimHub only scans its own directory.
3. Start SimHub and enable **Uniflag** in the *"New plugins have been
   detected!"* dialog (or later via **Settings → Plugins**).

## 2. Firmware

1. Hold the **BOOTSEL** button on the Cosmic Unicorn while plugging in
   the USB cable. It mounts as a drive named `RPI-RP2`.
2. Copy `uniflag.uf2` onto that drive. The panel reboots into the new
   firmware and SimHub finds it automatically — no port picking.

## 3. Overlay dash (optional)

1. Copy the whole `Uniflag Overlay` folder into
   `C:\Program Files (x86)\SimHub\DashTemplates\` (needs one admin
   file-copy prompt).
2. In SimHub → **Dash Studio**, add *Uniflag Overlay* as an overlay.

## Help

Full setup and troubleshooting guide:
<https://github.com/nickolaj-jepsen/uniflag/blob/main/simhub/README.md>
