# uniflag — design notes

This directory holds the documentation for the project. It splits into
two layers:

- **Hardware / protocol references**: the underlying facts about the
  Cosmic Unicorn panel and SimHub's wire protocol. Mostly stable —
  changes only when upstream changes.
- **Architecture and bring-up**: how *this* firmware works and what we
  hit during v1 bring-up. Living documents — update when the firmware
  changes.

| File | Layer | What's in it |
|------|-------|--------------|
| [`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md) | reference | Pin map, button layout, audio/I²C/light-sensor pins, framebuffer layout, BCM, gamma. **Includes the 4-byte alignment requirement on the DMA source.** |
| [`cosmic-unicorn-pio.md`](./cosmic-unicorn-pio.md) | reference | The bitstream PIO program transcribed and annotated, plus the SM configuration that pairs with it. |
| [`simhub-custom-serial.md`](./simhub-custom-serial.md) | reference | SimHub's Custom Serial Devices plugin: protocol, NCalc syntax, and a worked example. The end-user setup steps live in [`../simhub/README.md`](../simhub/README.md). |
| [`simhub-flag-properties.md`](./simhub-flag-properties.md) | reference | The flag-related properties exposed by SimHub's `DataCorePlugin` — both the unified `GameData.Flag_*` set and the per-sim raw-data fallbacks. |
| [`rust-embassy-approach.md`](./rust-embassy-approach.md) | architecture | **As-built** firmware architecture: crate layout, pinned versions, task topology, why USB lives in `main`, what's deferred. |
| [`bring-up-notes.md`](./bring-up-notes.md) | architecture | Debugging stories and gotchas worth not having to rediscover: the alignment bug, the DMA chain pattern, embassy-rp API quirks, diagnostic checkpoints. |

## Hardware target — note on revisions

The Cosmic Unicorn we're building for is the **original RP2040 + Pico W**
version (no longer in Pimoroni's shop; the listing now points at the
Pico-2-W revision). Both revisions share the panel, pinout, and PIO
program — only the host MCU changed (RP2040 → RP2350). All the work in
`cosmic-unicorn-*.md` applies to both, but the firmware crate
constraints (`embassy-rp` features = `["rp2040"]`, target triple
`thumbv6m-none-eabi`) are RP2040-specific.

## Project shape

```
uniflag/
├── docs/                ← you are here
├── firmware/            ← Rust + embassy crate, builds for thumbv6m-none-eabi
│   └── src/{main, display, render, buttons}.rs
├── proto/               ← shared no_std wire-protocol types + tests
├── sim/                 ← uniflag-sim host binary that pretends to be SimHub
└── simhub/              ← SimHub-side import notes (and the eventual exported JSON profile)
```

## Sources

- Pimoroni Pico SDK — [`libraries/cosmic_unicorn/`](https://github.com/pimoroni/pimoroni-pico/tree/main/libraries/cosmic_unicorn)
  (`cosmic_unicorn.hpp`, `.cpp`, `.pio`, README, plus `common/pimoroni_common.hpp`
  for the GAMMA_14BIT table).
- SimHub wiki — [Custom serial devices](https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices),
  [Custom Arduino Hardware Support](https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support),
  [NCalc scripting](https://github.com/SHWotever/SimHub/wiki/NCalc-scripting).
- SimHub manual — <https://manual.simhubdash.com>.
- `kjagiello/hub75-pio-rs` — pattern reference for the DMA-chain idiom
  (`src/dma.rs`). Doesn't apply directly because the Cosmic Unicorn
  isn't HUB75.
- Embassy-rs — [`embassy-rp` docs](https://docs.embassy.dev/embassy-rp/),
  [embassy repo](https://github.com/embassy-rs/embassy).
