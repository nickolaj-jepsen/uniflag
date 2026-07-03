> [!WARNING]
> **This project is vibecoded.** Run at your own risk. No warranties, no support, just a fun weekend hack.

# uniflag

A sim-racing flag-display for the Pimoroni Cosmic Unicorn (32×32 RGB matrix,
RP2040 / Pico W). Driven by [SimHub] over USB CDC.

> Targets the **original** Cosmic Unicorn (RP2040 + Pico W). The newer
> Pico-2-W revision is not supported.

[SimHub]: https://www.simhubdash.com

## Repo layout

- `firmware/` — embedded firmware (Rust + embassy-rs, RP2040)
- `proto/` — wire-protocol types shared between firmware and the simulator
- `plugin/` — SimHub plugin (C#, .NET Framework 4.8 — v2 work in progress)
- `sim/` — host-side simulator (`uniflag-sim`) that pretends to be SimHub
- `simhub/` — SimHub-side "Custom serial device" profile + setup notes
- `docs/` — external references: Cosmic Unicorn hardware, SimHub plugin / properties
- `justfile` — task runner (see below)

## Toolchain

```bash
nix develop      # devShell with rustup, elf2uf2-rs
```

Without Nix: install rustup, add the `thumbv6m-none-eabi` target, and
install `elf2uf2-rs` from cargo.

## Common tasks

All driven through [`just`](https://github.com/casey/just); run `just`
with no arguments to list them.

| Command          | What it does                                                              |
|------------------|---------------------------------------------------------------------------|
| `just fmt`       | `cargo fmt --all`                                                         |
| `just fmt-check` | `cargo fmt --all -- --check` (CI gate)                                    |
| `just clippy`    | clippy on host crates *and* firmware (different target), `-D warnings`    |
| `just test`      | `cargo test` on host crates only (firmware is `no_std`, `test = false`)   |
| `just build`     | release build of the firmware ELF                                         |
| `just img`       | build, then convert ELF → UF2 at `target/uniflag.uf2`                     |
| `just flash`     | full pipeline: build → UF2 → wait for `RPI-RP2` mount → copy → fix serial |
| `just sim`       | run `uniflag-sim` interactively against the device's serial port          |

The host crates (`proto`, `uniflag-sim`) are the workspace
default-members; the firmware is excluded so a bare `cargo check` from
the root doesn't try to cross-compile.

## Flashing

1. Hold **BOOTSEL** on the Cosmic Unicorn while plugging it in. The
   board enumerates as the `RPI-RP2` USB mass-storage device.
2. `just flash` — builds the firmware, converts to UF2, waits for the
   mount, copies, and waits for the serial device to come back.

The mount path and serial device in the `justfile` are Linux-flavoured
(`/run/media/$USER/RPI-RP2`, `/dev/ttyACM0`); on other OSes either edit
the `justfile` or run the steps by hand (`elf2uf2-rs` → drag-and-drop
the UF2).

## SimHub setup

See [`simhub/README.md`](simhub/README.md) for the end-user setup
(profile import, NCalc formula, troubleshooting). The firmware also
works against the bundled `uniflag-sim` host without SimHub running.

## External references

Cosmic Unicorn hardware / PIO and the SimHub plugin / property
catalogue live under [`docs/`](docs/README.md).

## License

uniflag is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)).
The SimHub plugin (`plugin/`) additionally carries a GPLv3 section 7
linking exception permitting distribution of builds that link SimHub's
proprietary plugin assemblies (`SimHub.Plugins.dll` etc.) — see
[LICENSE-EXCEPTION](LICENSE-EXCEPTION). Releases never include SimHub's
own DLLs; install [SimHub] to obtain them. Contributions to `plugin/`
are accepted under GPL-3.0-or-later including this exception.
