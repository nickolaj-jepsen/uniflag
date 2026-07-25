# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`uniflag` is a sim-racing flag display for the **original** Pimoroni Cosmic Unicorn (RP2040 + Pico W; the newer Pico-2-W / RP2350 revision is **not** supported). A native SimHub plugin (C#) owns all state and rendering; the firmware (Rust + embassy-rs) is a framebuffer device that receives complete RGB888 frames over USB-CDC via a frozen binary protocol. A browser/overlay virtual panel renders the same frames without hardware.

## Workspace layout

- `proto/` — the frozen v2 binary wire protocol (`no_std`, allocation-free): `packet` (typed packets + `PROTOCOL_VERSION`, `USB_VID`/`USB_PID`, panel geometry), `cobs`, `crc`. Golden-vector conformance tests in `proto/tests/golden_vectors.rs` pin the bytes under `testdata/proto/`.
- `firmware/` — embedded firmware (`thumbv6m-none-eabi`, embassy-rs): blits streamed frames, paints the local fallback/test screens, reports button events. Excluded from workspace `default-members` so a bare `cargo check` from the root doesn't try to cross-compile.
- `cli/` — `uniflag-cli`, host-side test-pattern streamer and protocol diagnostic (stream/loopback/emit modes). Conformance tests pin its emitted bytes against `testdata/proto/`.
- `plugin/` — the SimHub plugin (C#, .NET Framework 4.8): `plugin/src/` (Rendering, Adapters, Device, Protocol, Web, settings UI) + `plugin/tests/` (xunit). Built via `plugin/UniflagPlugin.sln`; needs SimHub's reference DLLs (see Common commands).
- `overlay/` — the browser virtual-panel page (`index.html`, embedded into the plugin assembly at build time) + the DashStudio dash under `overlay/dash/`.
- `simhub/` — end-user plugin install / setup / troubleshooting guide.
- `testdata/` — the frozen cross-language golden fixtures (see Golden fixtures below). Top-level so the C# tests reach it by relative path.
- `packaging/` — release-zip assembly: `package.ps1` (run by `just package` and the release workflow) and the `INSTALL.md` shipped inside the zip.
- `docs/` — protocol + effects specifications, SimHub / Cosmic Unicorn references, historical v2 plan ([index](docs/README.md)).

The Rust host crates (`proto`, `uniflag-cli`) are workspace `default-members`. To touch the firmware crate from the root, use `--manifest-path firmware/Cargo.toml` or `cd firmware && cargo ...`.

## Common commands

All wrapped by `just` (run `just` to list). The justfile works on both Linux and Windows; on Windows override the COM port and BOOTSEL drive via env vars before invoking, e.g. `$env:UNIFLAG_SERIAL = 'COM5'; $env:UNIFLAG_MOUNT = 'D:\'`.

| Command | Behaviour |
|---------|-----------|
| `just fmt` / `just fmt-check` | `cargo fmt --all` (the latter is the CI gate). |
| `just clippy` | clippy on host crates **and** firmware separately (different target). `-D warnings`. |
| `just test` | `cargo test -p proto -p uniflag-cli --all-targets`. Firmware has `test = false` (it's `no_std`). |
| `just build` / `just img` | release firmware ELF / ELF → UF2 at `target/uniflag.uf2`. |
| `just flash` | build → UF2 → wait for `RPI-RP2` mount (hold BOOTSEL) → copy → wait for serial. |
| `just cli` | run `uniflag-cli` against the device's serial port (streams a test pattern by default). |
| `just plugin-build` / `just plugin-test` | build / test the SimHub plugin (Windows only; `dotnet` against `plugin/UniflagPlugin.sln`). |
| `just overlay-serve` | serve `overlay/` over http:// for page iteration outside SimHub. |
| `just golden-regen` | regenerate the *regenerable* fixtures only — see Golden fixtures. Deliberate, reviewed commits only. |
| `just package` | assemble the release zip locally (Windows; same layout + only-one-DLL audit as the CI release job, via `packaging/package.ps1`). |

Run a single host test: `cargo test -p proto -- hello` (or `-p uniflag-cli`). Single plugin test: `dotnet test plugin/UniflagPlugin.sln -c Release "-p:SimHubDir=..." --filter "FullyQualifiedName~GoldenFrameTests"`.

The plugin build resolves SimHub's proprietary reference assemblies (`SimHub.Plugins.dll` etc.) from `$env:UNIFLAG_SIMHUB_DIR` (default `C:\Program Files (x86)\SimHub`). They are never committed or redistributed. Reference SimHub version: 9.11.21 (`docs/simhub-plugin-api.md`).

CI mirrors `just fmt-check`, `just clippy` (both legs), and `just test` — keep them green. The Windows plugin job is isolated and non-gating by design (installer flakiness must never red the Rust pipeline).

Toolchain: stable rustc with `thumbv6m-none-eabi` (pinned in `rust-toolchain.toml`); `nix develop` provides rustup and `elf2uf2-rs`. Plugin side: .NET Framework 4.8 developer pack + a SimHub install (Windows).

## Architecture

### Wire protocol (`proto/`)

Binary, COBS-framed, CRC-16/CCITT-FALSE, one trailing `0x00` delimiter per packet. Five packet types: Hello / HelloAck (handshake carrying `PROTOCOL_VERSION`, firmware version, panel size), Frame (3072 B RGB888, row-major), Brightness, ButtonEvent. The full layout lives in `docs/protocol.md`; `proto/src/packet.rs` is the Rust source of truth, mirrored by hand in C# under `plugin/src/Protocol/`.

**Frozen as of M6** — any wire-visible change bumps `PROTOCOL_VERSION`, changes both codecs *and* the prose *and* regenerates `testdata/proto/` in one reviewed commit. Forward-compat posture: unknown packet types ignored; bad CRC / wrong length dropped silently; receivers resync at the next `0x00`. The USB identity (`USB_VID`/`USB_PID` in `proto`, mirrored in `plugin/src/Device/DeviceDiscovery.cs`) is single-sourced — the plugin accepts both the test PID `0x0001` and the registered `0xF1A6` during the pid.codes transition (`docs/v2-tracking.md`).

### Firmware (`firmware/src/`)

Embassy executor; three spawned tasks plus three futures joined in `main` (`join3` dodges `'static` gymnastics on `Receiver`/`Sender`/`UsbDevice`):

1. **`runtime::run`** (`runtime.rs`) — owns the `Display`. Select loop: streamed Frame → blit + `present().await`; Brightness → applied on next blit (never to local screens — they always paint full-brightness so a stale sleep value can't hide them); long press → toggle the local test screen; 16 ms tick → advance the local frame counter and repaint whichever local screen is active. No decodable Frame for `CONNECT_TIMEOUT` (1.5 s) → the roaming-ember fallback screen (`screens.rs`, `docs/flag-grammar.md` §7a), also the boot state.
2. **`buttons::run`** (`buttons.rs`) — debounces the three buttons (GPIO 21/26/27, active-low), classifies short/long on release, queues `ButtonEvent`s for TX (only while a handshaken host is live). Button *policy* is host-side; the firmware only reports.
3. **`watchdog_feed`** — feeds the RP2040 watchdog every 2 s (8 s timeout). The panic handler relies on it: on panic the firmware spins, the feed stops, the chip resets.

Plus `run_usb`, `cdc_rx_loop` (COBS-accumulate → parse → dispatch: Frame to a zero-copy two-slot channel, Brightness to a signal, Hello answered with HelloAck on `TX_CHANNEL`), and `cdc_tx_loop`.

**Display driver** (`display.rs`) — the Cosmic Unicorn is **not** HUB75: column shift-registers + 4-to-16 row decoder, one PIO SM with a self-chaining DMA pair, double-buffered bitstreams, per-pixel 14-bit-BCM gamma. See `docs/cosmic-unicorn-hardware.md` / `docs/cosmic-unicorn-pio.md`.

The firmware deliberately does **not** render effects and does not link any render code — the fallback/test screens in `screens.rs` implement `docs/flag-grammar.md` §7 state (a) bit-exactly with embedded literals and LUT-free integer math. The device stores no settings; brightness persists host-side in the plugin.

### Plugin (`plugin/src/`)

- **Rendering** — the renderer core: `RendererLoop` runs a dedicated thread ticking the **60 fps internal frame counter** all animation math assumes, painting through the **Grammar renderer** (`Rendering/Grammar/`: `Compositor` → `EnvelopeTracker` → `Painter`, spec `docs/flag-grammar.md`) or `Idles.PaintConnectedIdle` (stream alive, no game), publishing 3072-byte frames to registered `IFrameSink`s. Sinks *sample* that clock (USB at 30 fps by parity decimation, web overlay at 30 fps, WPF preview at display cadence) — never rebase the counter to a sink rate; that would halve every strobe rate. The thread runs only while ≥ 1 sink is registered. `Rendering/` must stay WPF-free (loadable from plain xunit).
- **Adapters** — telemetry → `SignalState`: `GenericAdapter` (unified `Flag_*` properties, priority/tier + session mapping documented in `docs/simhub-flag-properties.md`) with the iRacing raw-telemetry refiner layered after it (tiers, SC regime, penalties, start sequence, countdown notices). One reused snapshot, immutable pipeline — no avoidable allocation on the 60 Hz SimHub data thread.
- **Device** — `DeviceConnectionManager`: VID/PID discovery (manual COM override bypasses the filter but never the handshake), Hello/HelloAck validation (protocol-version / panel-size mismatch → *Refused* with message, never silent), 30 fps frame streaming, reconnects; `BrightnessPolicy` owns the slider + device-button policy and persists into SimHub settings.
- **Web** — `OverlayWebServer`: localhost-only (`127.0.0.1:8972`), hand-rolled HTTP + WebSocket, newest-frame-wins per client. Contracts in `docs/web-overlay.md`.
- `UniflagPlugin.cs` wires it together (SimHub `IPlugin`/`IDataPlugin`/`IWPFSettingsV2`); the settings tab shows device status, brightness, port override, live preview, overlay status.

### Golden fixtures (`testdata/`) — the cross-language contract

Both the Rust and C# suites consume the same files; **neither side ever generates its own fixtures**; regeneration only via `just golden-regen` in deliberate, reviewed commits. `.gitattributes` marks `testdata/**` `-text`.

- `testdata/proto/` — byte vectors for every packet (pre- and post-COBS) plus negative vectors; prose spec `docs/protocol.md`. Regenerable (`golden-regen` proto leg) — but the bytes are the frozen contract, so a regen that changes anything is a protocol change.
- `testdata/timelines/` — `SignalState` scenario scripts (JSON, format 2) replayed through the adapter pipeline by `plugin/tests/TimelineReplayTests.cs`. C#-only (the one non-cross-language subdir) and hand-authored, with no `golden-regen` leg; schema revisions and new scenarios happen in deliberate reviewed commits.

**The renderer is deliberately not pinned to bytes.** `testdata/frames-grammar/` was retired (`docs/flag-grammar.md` §11): while the visual design is still moving, a 41-binary corpus turned every tweak into a mass regeneration whose diff read `Binary files differ`. The painters are covered instead by the `GrammarSmokeTests` whole-scenario replay (churn-free invariants), the per-painter unit tests, and *visual* review via `just frames-sheet`. Don't reintroduce a frame corpus without a reason — the wire protocol is a byte contract, the picture isn't. (Earlier corpora `testdata/frames/` and `testdata/frames-plugin/` went the same way at the flag-grammar cutover; git history keeps all the bytes.)

## Conventions

- `clippy.toml` warns on `unwrap_used` workspace-wide (allowed in tests). Don't reintroduce naked `.unwrap()` in non-test code.
- Firmware is `no_std`, no `alloc`. `heapless` for buffers; `static_cell` / `StaticCell` for `'static` allocations needed by USB descriptors. RP2040 has no atomic CAS — `portable-atomic` with the `critical-section` feature emulates it via embassy-rp's `critical-section-impl`. Don't pull in dependencies that assume native atomics.
- The `pio` version must match what `embassy-rp` uses (currently 0.3) — a mismatched `Program` type breaks `Common::load_program`. (`pio-proc` is not a direct dep; `pio::pio_asm!` re-exports it.)
- No on-device logging or debugger support: firmware doesn't pull in `defmt` / `defmt-rtt` / `panic-probe`, and there's no `probe-rs` runner. Errors are silently swallowed; panic spins until the watchdog resets. Don't reintroduce these without a reason — the maintainer doesn't own a debugger.
- Dev profile uses `opt-level = 1` because async generators inflate badly at `0`. Don't change without a reason.
- `testdata/**` is immutable outside `just golden-regen`: never edit fixtures by hand, never let a test write them (regen paths are env-var-gated). The one sanctioned hand-authored path is `testdata/timelines/` (see Golden fixtures). `golden-regen` now covers `testdata/proto/` only.
- The wire protocol is frozen: extending it means bumping `PROTOCOL_VERSION`, updating `proto` + the C# mirror + `docs/protocol.md` + `testdata/proto/` together, in one commit.
- Plugin sources carry `SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception` headers (see `LICENSE-EXCEPTION`); keep them on new `plugin/` files.
- v1 history: the ASCII line protocol, the `render/` crate, and the SimHub Custom Serial device profile were retired at M11 — they live in git history only. Old comments citing `render/src/*.rs` paths refer to that history (`docs/effects-spec.md` explains the convention). The ported legacy renderer (`Effects`/`IdleEffects`/`Precedence`/`RenderState`) and its corpora (`testdata/frames/`, `testdata/frames-plugin/`) were likewise retired at the flag-grammar cutover — `docs/flag-grammar.md` is the live spec; `docs/effects-spec.md` is historical.
