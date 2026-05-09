# uniflag

A sim-racing flag-display for the Pimoroni Cosmic Unicorn (32×32 RGB matrix,
RP2040 / Pico W). Driven by [SimHub] over USB CDC.

> Targets the **original** Cosmic Unicorn (RP2040 + Pico W). The newer
> Pico-2-W revision is not supported.

[SimHub]: https://www.simhubdash.com

## Repo layout

- `firmware/` — the embedded firmware (Rust + embassy-rs, RP2040)
- `proto/` — wire-protocol types shared between firmware and the simulator
- `sim/` — host-side simulator (`uniflag-sim`) that pretends to be SimHub
- `simhub/` — the SimHub-side "Custom serial device" profile + import notes
- `docs/` — research notes and hardware reference

## Quick start

```bash
nix develop                               # devShell with rustup, probe-rs, elf2uf2-rs
just flash                                # build firmware → UF2 → wait → copy to BOOTSEL
just sim                                  # run the simulator interactively against /dev/ttyACM0
```

See [`docs/README.md`](docs/README.md) for the hardware reference and
[`simhub/README.md`](simhub/README.md) (TBD) for SimHub setup.
