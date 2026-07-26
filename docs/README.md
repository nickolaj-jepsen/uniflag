# Docs index

Two kinds of documents live here: **project docs** (how the wire protocol
and the flag grammar currently work — written down so the firmware, the
plugin and the docs stay in step) and **external references** (the Cosmic
Unicorn panel and SimHub, written down because upstream doesn't).

The project docs describe a prerelease hobby project and will keep
changing with it. They're a record of the current design and the
reasoning behind it, not a standard anyone has to live up to.

## Project docs

| File | Subject |
|------|---------|
| [`protocol.md`](./protocol.md) | The binary wire protocol: COBS framing, CRC-16, packet layouts, USB identity, handshake. Changing it costs a `PROTOCOL_VERSION` bump; the byte vectors in `testdata/proto/` are what both codecs get checked against. |
| [`flag-grammar.md`](./flag-grammar.md) | **The renderer's design doc** — the signal language: the six grammar rules, tiers and the envelope, slots/precedence/suppression, the signal catalogue, the idle family, `SignalState`, adapter contracts. Implemented by `plugin/core/Rendering/Grammar/`; §7a is implemented by the firmware fallback screen. |
| [`web-overlay.md`](./web-overlay.md) | The browser/overlay virtual panel: `OverlayWebServer`, the LED-dot page, the DashStudio dash, and the design contracts (localhost-only, newest-frame-wins, 30 fps). |

## External references

| File | Subject |
|------|---------|
| [`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md) | Pin map, button layout, framebuffer / bitstream layout, BCM, gamma. **Includes the 4-byte alignment requirement on the DMA source.** |
| [`cosmic-unicorn-pio.md`](./cosmic-unicorn-pio.md) | The Cosmic Unicorn bitstream PIO program, transcribed and annotated, plus the SM configuration that pairs with it. |
| [`simhub-plugin-api.md`](./simhub-plugin-api.md) | SimHub's undocumented plugin API: loading contract, interfaces, settings persistence, UI integration. Reference SimHub version 9.11.21. |
| [`simhub-flag-properties.md`](./simhub-flag-properties.md) | The flag-related properties exposed by SimHub's `DataCorePlugin` — the unified `GameData.Flag_*` set and the per-sim raw-data fallbacks, written down because upstream doesn't. |

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
- SimHub — the plugin SDK demo shipped inside the install
  (`PluginSdk\User.PluginSdkDemo`), reflection over `SimHub.Plugins.dll`,
  and the [manual](https://manual.simhubdash.com).
- `kjagiello/hub75-pio-rs` — pattern reference for the DMA-chain idiom
  (`src/dma.rs`). Doesn't apply directly because the Cosmic Unicorn
  isn't HUB75.
- Embassy-rs — [`embassy-rp` docs](https://docs.embassy.dev/embassy-rp/),
  [embassy repo](https://github.com/embassy-rs/embassy).
