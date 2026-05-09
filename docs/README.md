# External references

This directory holds reference material for the external systems
uniflag integrates with: the Cosmic Unicorn panel and SimHub. It
intentionally does **not** describe how the firmware is currently
built — that lives in the code itself.

| File | Subject |
|------|---------|
| [`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md) | Pin map, button layout, framebuffer / bitstream layout, BCM, gamma. **Includes the 4-byte alignment requirement on the DMA source.** |
| [`cosmic-unicorn-pio.md`](./cosmic-unicorn-pio.md) | The Cosmic Unicorn bitstream PIO program, transcribed and annotated, plus the SM configuration that pairs with it. |
| [`simhub-custom-serial.md`](./simhub-custom-serial.md) | SimHub's Custom Serial Devices plugin: protocol, NCalc syntax, gotchas. End-user setup steps live in [`../simhub/README.md`](../simhub/README.md). |
| [`simhub-flag-properties.md`](./simhub-flag-properties.md) | The flag-related properties exposed by SimHub's `DataCorePlugin` — both the unified `GameData.Flag_*` set and the per-sim raw-data fallbacks. |

## Hardware target — note on revisions

The Cosmic Unicorn we target is the **original RP2040 + Pico W**
version (no longer in Pimoroni's shop; the listing now points at the
Pico-2-W revision). Both revisions share the panel, pinout, and PIO
program — only the host MCU changed (RP2040 → RP2350). Everything in
`cosmic-unicorn-*.md` applies to both; the Cargo configuration in
`firmware/` is RP2040-specific.

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
