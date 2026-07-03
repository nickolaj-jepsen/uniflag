# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`uniflag` is a sim-racing flag display for the **original** Pimoroni Cosmic Unicorn (RP2040 + Pico W; the newer Pico-2-W / RP2350 revision is **not** supported). Firmware is Rust + embassy-rs; it consumes a small ASCII line protocol over USB-CDC, typically driven by SimHub's built-in Custom Serial Devices plugin.

## Workspace layout

- `proto/` — wire-protocol types (`no_std`, allocation-free), shared by firmware, render, and cli.
- `render/` — `uniflag-render`, host-testable rendering primitives (`anim`, `effects`, `surface::Surface`, `BrightnessController`). `no_std`, depends only on `proto`. Snapshot tests in `render/tests/` use `insta` against an in-memory `MockSurface` (defined in `tests/common/mod.rs`).
- `firmware/` — embedded firmware (`thumbv6m-none-eabi`, embassy-rs). Implements `Surface` for the hardware `Display` and runs the embassy task in `runtime.rs` that ties signals/timers/persistence to `uniflag_render::effects::paint`. Excluded from workspace `default-members` so a bare `cargo check` from the root doesn't try to cross-compile.
- `cli/` — `uniflag-cli`, binary-protocol test-pattern streamer and firmware diagnostic tool (stream/loopback/emit).
- `simhub/` — end-user SimHub profile + setup notes.
- `docs/` — external references (Cosmic Unicorn hardware/PIO, SimHub plugin/property catalogue).

The host crates (`proto`, `uniflag-render`, `uniflag-cli`) are workspace `default-members`. To touch the firmware crate from the root, use `--manifest-path firmware/Cargo.toml` or `cd firmware && cargo ...`.

## Common commands

All wrapped by `just` (run `just` to list). The justfile works on both Linux and Windows; on Windows override the COM port and BOOTSEL drive via env vars before invoking, e.g. `$env:UNIFLAG_SERIAL = 'COM5'; $env:UNIFLAG_MOUNT = 'D:\'`.

| Command | Behaviour |
|---------|-----------|
| `just fmt` / `just fmt-check` | `cargo fmt --all` (the latter is the CI gate). |
| `just clippy` | clippy on host crates **and** firmware separately (different target). `-D warnings`. |
| `just test` | `cargo test -p proto -p uniflag-render -p uniflag-cli`. Firmware has `test = false` (it's `no_std`). |
| `just build` | release build of the firmware ELF. |
| `just img` | build, then convert ELF → UF2 at `target/uniflag.uf2`. |
| `just flash` | full pipeline: build → UF2 → wait for `RPI-RP2` mount (hold BOOTSEL) → copy → wait for serial. |
| `just cli` | run `uniflag-cli` against the device's serial port (streams a test pattern by default). |

Run a single host test: `cargo test -p proto -- parse_canonical_line` (or `-p uniflag-cli`).

CI mirrors `just fmt-check`, `just clippy` (both legs), and `just test` — keep them green.

Toolchain: stable rustc with `thumbv6m-none-eabi` (pinned in `rust-toolchain.toml`). `nix develop` provides rustup and `elf2uf2-rs`; without Nix install those manually.

## Architecture

### Wire protocol (`proto/src/lib.rs`)

ASCII, semicolon-separated `key=value` fields, terminated with `\n` (parser also accepts `\r\n`). `no_std`, no allocation, ≤ `MAX_LINE_LEN` (48) bytes. Fields:

| Key | Values | Meaning |
|-----|--------|---------|
| `F` | `N Y B K W R G C O` | flag (none / yellow / blue / black / white / red / green / chequered / orange) |
| `B` | `0 1 2` | wave level (none / single / double) |
| `S` | `pre-race racing paused post-race replay unknown` | session state |
| `C` | `N V S` | caution (none / VSC / Safety Car) — orthogonal to `F` |
| `Z` | `(empty) 1 2 3 12 13 23 123` | sector mask — ascending unique digits, packed in low 3 bits |

**Forward-compat invariants** — preserve these when extending:
- Unknown keys are silently ignored.
- Unknown values for a known key return `ParseError::BadValue`.
- The line protocol is the single source of truth between firmware and any host. Tests in `proto` round-trip every flag/session/wave/caution/sector combination — extend them when adding values.

### Firmware (`firmware/src/`)

Embassy executor with three concurrent jobs spawned from `main`:

1. **`runtime_task`** (`runtime.rs`) — owns the `Display` and the persisted brightness. 60 fps animation loop; calls `uniflag_render::effects::paint` to recompute the whole panel from current `proto::State` + frame counter every tick. Per-flag base layers + overlays + precedence rules are documented at the top of `runtime.rs` (red beats caution beats per-flag base; sector band overlays unless red; "disconnected" is the boot/idle blank state when no host updates arrive within `CONNECT_TIMEOUT`). Brightness/sleep/persist policy is delegated to `uniflag_render::BrightnessController`.
2. **`buttons::run`** (`buttons.rs`) — polls the three brightness buttons (GPIO 21/26/27, active-low) and pushes `uniflag_render::brightness::Action` events into a channel.
3. **`watchdog_feed`** — feeds the RP2040 watchdog every 2 s (8 s timeout, just under the RP2040 errata-E1 ceiling). The panic handler also relies on this for recovery — on panic it spins, the feed task stops, and the chip resets within `WATCHDOG_TIMEOUT`.

Two more futures are awaited in `main` directly (rather than spawned as tasks) to dodge `'static` lifetime gymnastics on `Receiver` / `UsbDevice`: `run_usb` and `cdc_rx_loop`.

State plumbing is one-way:
- `cdc_rx_loop` parses incoming lines and `signal()`s `STATE_SIGNAL` (a `Signal<CriticalSectionRawMutex, proto::State>`).
- The runtime task `wait()`s on `STATE_SIGNAL` and on the brightness channel; both racing against a frame-tick timer.
- Brightness is debounced (`BrightnessController::SAVE_DEBOUNCE_MS = 2000`) before being persisted.

**Display driver** (`display.rs`) — the Cosmic Unicorn is **not** HUB75. A custom column shift-register topology with 4-to-16 row decoder, driven by a single PIO state machine with a self-chaining DMA pair. Two SRAM bitstreams (front/back) double-buffer; `present()` atomically retargets the DMA chain at the just-painted buffer and waits for the swap. Per-pixel BCM (binary code modulation) splits 14-bit gamma-corrected intensity across 14 BCD frames; full bitstream is 16128 B. See `docs/cosmic-unicorn-hardware.md` and `docs/cosmic-unicorn-pio.md` for the wire-level details.

**Persistent storage** (`storage.rs`) — the last 4 KB QSPI flash sector is reserved for settings (currently just brightness). Reservation is by linker-script: `firmware/memory.x` shrinks the `FLASH` region by `0x1000`, and `STORAGE_OFFSET` points at the carved sector. Erase + write blocks XIP for ~25 ms — debounce writes; the watchdog feed cadence is sized for it. Keep the `memory.x` reservation and `STORAGE_OFFSET` in lockstep.

### CLI (`cli/src/`)

Speaks the v2 binary protocol (`proto::packet`). Three modes: stream (default — Hello/HelloAck handshake, then COBS-framed Frame packets at a monotonic-deadline-paced 30 fps, printing inbound ButtonEvents), loopback (decodes its own emitted byte stream in-process, no serial), and emit (writes exactly one encoded wire packet and exits, for golden byte-diff verification). Output goes to a serial port (`--port`) or stdout (`--no-port`); all human status goes to stderr. Testable logic (patterns, pacing, RX decode, wire builders) lives in `cli/src/lib.rs` modules; conformance tests in `cli/tests/` pin the emitted bytes against `testdata/proto/`.

## Conventions

- `clippy.toml` warns on `unwrap_used` workspace-wide (allowed in tests). Don't reintroduce naked `.unwrap()` in non-test code.
- Firmware is `no_std`, no `alloc`. `heapless` for buffers; `static_cell` / `StaticCell` for `'static` allocations needed by USB descriptors. RP2040 has no atomic CAS — `portable-atomic` with the `critical-section` feature emulates it via embassy-rp's `critical-section-impl`. Don't pull in dependencies that assume native atomics.
- The `pio` / `pio-proc` versions must match the version `embassy-rp` re-exports (currently 0.3) — bumping one without the other breaks `Common::load_program`.
- No on-device logging or debugger support: firmware doesn't pull in `defmt` / `defmt-rtt` / `panic-probe`, and there's no `probe-rs` runner. Errors are silently swallowed; panic spins until the watchdog resets. Don't reintroduce these without a reason — the maintainer doesn't own a debugger.
- Dev profile uses `opt-level = 1` because async generators inflate badly at `0`. Don't change without a reason.
- The `simhub/uniflag.shsds` profile is an opaque GUI export — author it through SimHub on Windows and copy the file in, don't hand-edit. The `.shsds` JSON format is undocumented and changes between SimHub versions.
- SimHub does **not** auto-append a line terminator; any NCalc formula must end with `'\r\n'` or the firmware never sees a complete line. This is the single most common SimHub-side failure — keep the warning prominent in `simhub/README.md`.
