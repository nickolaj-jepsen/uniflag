> [!WARNING]
> **This project is vibecoded.** Run at your own risk. No warranties, no support, just a fun weekend hack.

# uniflag

A sim-racing flag display for the Pimoroni Cosmic Unicorn (32×32 RGB
matrix, RP2040 / Pico W), driven by a native [SimHub] plugin.

The plugin owns all state and rendering: it reads flag telemetry from
the running sim, renders 32×32 frames at 60 fps, and streams them over
USB (binary protocol, COBS-framed, CRC-checked) to the panel — which is
a dumb framebuffer device. The same frames also feed a browser-rendered
virtual panel served at `http://127.0.0.1:8972/`, usable standalone or
as a DashStudio in-game overlay, so you get a flag display even without
the hardware.

> Targets the **original** Cosmic Unicorn (RP2040 + Pico W). The newer
> Pico-2-W revision is not supported.

[SimHub]: https://www.simhubdash.com

## Install (prebuilt)

Grab `uniflag-<version>.zip` from the
[releases page](https://github.com/nickolaj-jepsen/uniflag/releases) — one
zip with the plugin DLL, the firmware UF2, the DashStudio overlay dash,
and an `INSTALL.md`. Plugin and firmware are versioned together; always
install both from the same zip. Full guide:
[`simhub/README.md`](simhub/README.md). Building from source: read on.

## Repo layout

- `proto/` — the frozen v2 binary wire protocol (Rust, `no_std`):
  packet layer, COBS framing, CRC-16, shared USB/geometry constants
- `plugin/` — the SimHub plugin (C#, .NET Framework 4.8): renderer,
  game adapters, USB device connection, web overlay server, settings UI
- `firmware/` — embedded firmware (Rust + embassy-rs, RP2040): receives
  frames over USB CDC and puts them on the panel
- `cli/` — host-side bring-up CLI (`uniflag-cli`): test-pattern streamer
  and protocol diagnostic for the panel
- `overlay/` — the browser virtual-panel page + the DashStudio dash file
- `simhub/` — end-user plugin install / setup / troubleshooting guide
- `testdata/` — frozen golden fixtures: the cross-language protocol byte
  vectors (the Rust and C# suites both verify against them), plus
  C#-only adapter timeline scripts
- `docs/` — protocol + effects specifications, SimHub and Cosmic Unicorn
  references ([index](docs/README.md))
- `packaging/` — release-zip assembly (`package.ps1`) and the `INSTALL.md`
  shipped inside the zip
- `justfile` — task runner (see below)

## Toolchain

Rust side (proto, cli, firmware):

```bash
nix develop      # devShell with rustup, elf2uf2-rs
```

Without Nix: install rustup, add the `thumbv6m-none-eabi` target, and
install `elf2uf2-rs` from cargo.

Plugin side (Windows): .NET Framework 4.8 developer pack plus a SimHub
install for the reference assemblies (`SimHub.Plugins.dll` etc. —
never redistributed). The build defaults to
`C:\Program Files (x86)\SimHub`; override with
`$env:UNIFLAG_SIMHUB_DIR`.

## Common tasks

All driven through [`just`](https://github.com/casey/just); run `just`
with no arguments to list them.

| Command              | What it does                                                              |
|----------------------|---------------------------------------------------------------------------|
| `just fmt`           | `cargo fmt --all`                                                         |
| `just fmt-check`     | `cargo fmt --all -- --check` (CI gate)                                    |
| `just clippy`        | clippy on host crates *and* firmware (different target), `-D warnings`    |
| `just test`          | `cargo test` on host crates only (firmware is `no_std`, `test = false`)   |
| `just build`         | release build of the firmware ELF                                         |
| `just img`           | build, then convert ELF → UF2 at `target/uniflag.uf2`                     |
| `just flash`         | full pipeline: build → UF2 → wait for `RPI-RP2` mount → copy → fix serial |
| `just cli`           | run `uniflag-cli` against the device's serial port                        |
| `just plugin-build`  | build the SimHub plugin DLL (Windows)                                     |
| `just plugin-test`   | run the plugin test suite (Windows)                                       |
| `just overlay-serve` | serve the overlay page from disk for iteration outside SimHub            |
| `just golden-regen`  | regenerate the *regenerable* golden fixtures — deliberate commits only    |
| `just package`       | assemble the release zip locally (Windows; same layout + audit as CI)     |

## Flashing

1. Hold **BOOTSEL** on the Cosmic Unicorn while plugging it in. The
   board enumerates as the `RPI-RP2` USB mass-storage device.
2. `just flash` — builds the firmware, converts to UF2, waits for the
   mount, copies, and waits for the serial device to come back.

The default mount path and serial device in the `justfile` are
Linux-flavoured; on Windows override them first, e.g.
`$env:UNIFLAG_SERIAL = 'COM5'; $env:UNIFLAG_MOUNT = 'D:\'`.

## SimHub setup

See [`simhub/README.md`](simhub/README.md) for the end-user guide:
installing the plugin DLL, panel auto-discovery, brightness, the web
overlay, and troubleshooting. The panel can also be driven without
SimHub via the bundled `uniflag-cli` test-pattern streamer.

## Specifications and references

The binary wire protocol is specified in
[`docs/protocol.md`](docs/protocol.md), the renderer's signal language in
[`docs/flag-grammar.md`](docs/flag-grammar.md); Cosmic Unicorn hardware
/ PIO and SimHub references live under [`docs/`](docs/README.md).

## License

uniflag is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)).
The SimHub plugin (`plugin/`) additionally carries a GPLv3 section 7
linking exception permitting distribution of builds that link SimHub's
proprietary plugin assemblies (`SimHub.Plugins.dll` etc.) — see
[LICENSE-EXCEPTION](LICENSE-EXCEPTION). Releases never include SimHub's
own DLLs; install [SimHub] to obtain them. Contributions to `plugin/`
are accepted under GPL-3.0-or-later including this exception.
