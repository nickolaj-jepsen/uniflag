# uniflag — research notes

A simracing flag-display built on the **Pimoroni Cosmic Unicorn** (32×32 RGB LED matrix,
RP2040 / Pico W, **original/old** revision — *not* the Pico 2 W variant), driven by **SimHub** over
USB serial. Firmware target: **Rust + embassy-rs**.

This directory holds the documentation gathered while scoping the project. Read in order:

| File | What's in it |
|------|--------------|
| [`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md) | Pin map, button layout, audio/I²C/light-sensor pins, framebuffer layout, BCM, gamma. |
| [`cosmic-unicorn-pio.md`](./cosmic-unicorn-pio.md) | The bitstream PIO program, transcribed and annotated. The thing we have to re-implement in Rust. |
| [`simhub-custom-serial.md`](./simhub-custom-serial.md) | How SimHub's "Custom serial devices" plugin talks to a USB-CDC device, NCalc formula syntax, and a complete worked Arduino-style example. |
| [`simhub-flag-properties.md`](./simhub-flag-properties.md) | The flag-related properties exposed by SimHub's `DataCorePlugin` — both the unified `GameData.Flag_*` set and the per-sim raw-data fallbacks. |
| [`rust-embassy-approach.md`](./rust-embassy-approach.md) | What exists in the Rust embedded ecosystem that we can reuse, what we'll have to write ourselves, and the recommended firmware architecture. |

## Hardware target — note on revisions

The Cosmic Unicorn we're building for is the **original RP2040 + Pico W** version
(no longer in Pimoroni's shop; the listing now points at the Pico 2 W revision).
Both revisions share the same panel, same pinout, same PIO program — only the
host MCU changed (RP2040 → RP2350). All the work in `cosmic-unicorn-*.md`
applies to both, but the firmware crate constraints (`embassy-rp`, target
triple `thumbv6m-none-eabi`) are RP2040-specific.

## Project shape (target)

```
uniflag/
├── docs/                    ← you are here
├── firmware/                ← Rust + embassy crate, builds for thumbv6m-none-eabi
│   ├── src/
│   │   ├── main.rs
│   │   ├── display/         ← cosmic_unicorn PIO driver + framebuffer
│   │   ├── protocol/        ← SimHub serial frame parser
│   │   └── flags/           ← flag → screen mapping
│   ├── Cargo.toml
│   ├── memory.x
│   └── build.rs
└── simhub/                  ← SimHub-side "custom serial device" profile (.shdevice or NCalc)
```

## Sources

Primary references (all consulted while writing these notes):

- Pimoroni Pico SDK — [`libraries/cosmic_unicorn/`](https://github.com/pimoroni/pimoroni-pico/tree/main/libraries/cosmic_unicorn)
  (`cosmic_unicorn.hpp`, `.cpp`, `.pio`, README)
- SimHub wiki — [Custom serial devices](https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices),
  [Custom Arduino Hardware Support](https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support),
  [NCalc scripting](https://github.com/SHWotever/SimHub/wiki/NCalc-scripting)
- SimHub manual — <https://manual.simhubdash.com>
- `kjagiello/hub75-pio-rs` — closest existing Rust HUB75 driver. **Does not apply to Cosmic Unicorn**
  (different topology — see `rust-embassy-approach.md`).
- Embassy-rs — [`embassy-rp` docs](https://docs.embassy.dev/embassy-rp/), [embassy repo](https://github.com/embassy-rs/embassy)
