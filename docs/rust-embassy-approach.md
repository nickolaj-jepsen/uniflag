# Firmware architecture — as built

This file describes the firmware as it actually exists in `firmware/` after
the v1 bring-up. The original planning sketch lived here too; the bits
that didn't survive contact with hardware have been pruned. For the
debugging story (alignment bug, DMA chain race), see
[`bring-up-notes.md`](./bring-up-notes.md).

Target: original Pimoroni Cosmic Unicorn (RP2040 / Pico W aboard).
Toolchain: `thumbv6m-none-eabi`, stable Rust, `nix develop` for the
devShell.

## Crate layout

```
firmware/
├── Cargo.toml              ← name = "uniflag-firmware", bin = "uniflag"
├── memory.x                ← 2 MB flash (Pico W's W25Q16JV) + 264 KB SRAM
├── build.rs                ← copies memory.x and pulls in defmt's linker fragment
├── .cargo/config.toml      ← target = thumbv6m-none-eabi, runner = probe-rs
└── src/
    ├── main.rs             ← init, USB CDC + parser, task spawn
    ├── display.rs          ← Cosmic Unicorn driver (PIO + DMA + framebuffer + gamma)
    ├── render.rs           ← flag → pixels (incl. brightness state)
    └── buttons.rs          ← polled debounce → BrightnessAction events
```

`firmware/` is a member of the workspace at the repo root. The workspace
default-members exclude it (`["proto", "sim"]`) so `cargo check` from the
root doesn't try to cross-compile.

## Pinned crate versions (May 2026)

These versions interlock; bumping one usually means bumping all of them.

```toml
embassy-rp        = { version = "0.10.0", features = ["defmt", "unstable-pac", "time-driver", "critical-section-impl", "rp2040"] }
embassy-executor  = { version = "0.10.0", features = ["platform-cortex-m", "executor-thread", "executor-interrupt", "defmt"] }
embassy-time      = "0.5.1"
embassy-sync      = "0.8.0"
embassy-usb       = "0.6.0"
embassy-futures   = "0.1.2"

# Match embassy-rp's internal pio dep (0.3). The 0.2 macro yields the
# wrong Program type and fails to compile against embassy-rp.
pio       = "0.3"
pio-proc   = "0.3"

defmt        = "1.0.1"
defmt-rtt    = "1.0.0"
panic-probe  = { version = "1.0.0", features = ["print-defmt"] }

cortex-m         = { version = "0.7.6", features = ["inline-asm"] }
cortex-m-rt      = "0.7.5"
critical-section = "1.1"
static_cell      = "2.1.1"
heapless         = "0.8"

# RP2040 (Cortex-M0+) has no atomic CAS; portable-atomic with the
# critical-section feature provides the polyfill needed by static_cell,
# embassy-sync etc.
portable-atomic = { version = "1.5", features = ["critical-section"] }
```

## Task topology

```
                ┌──────────────────────────┐
                │  embassy executor (1 core)│
                └──────────────────────────┘
                         │
        ┌────────────────┼────────────────┐
        │                │                │
   render_task      buttons::run    main(): join(usb.run, cdc_rx_loop)
   (owns Display)   (poll GPIOs)    (owns UsbDevice + Receiver)
        ▲                │                │
        │ STATE_SIGNAL   │ BRIGHTNESS_    │
        │                ▼ CHAN           ▼
        └────── ◄───────┴── parses ──── proto::State::parse
                                         (sync, no_std)
```

Tasks communicate through statics:

- `STATE_SIGNAL: Signal<CriticalSectionRawMutex, proto::State>`
  CDC RX → render. Latest-wins; if states arrive faster than the
  renderer can paint, intermediate ones are coalesced.
- `BRIGHTNESS_CHAN: Channel<CriticalSectionRawMutex, BrightnessAction, 4>`
  buttons → render. Used as a queue (a press should never be lost) so
  this is a `Channel`, not a `Signal`.

The display PIO + DMA chain runs continuously in hardware; it doesn't
participate in the task graph. CPU only touches the bitstream when
`render::paint` is called.

## Why USB lives in `main` and not in a `#[task]`

`UsbDevice<'static, _>` and `Receiver<'static, _>` carry types that are
fiddly to spell out for `#[embassy_executor::task]`'s static lifetime
checks. Joining them inline at the top of `main` with
`embassy_futures::join::join` is materially equivalent (both end up
running on the executor) and a lot less ceremony. See
[`firmware/src/main.rs`](../firmware/src/main.rs#L88).

## Display driver — what's special

See [`firmware/src/display.rs`](../firmware/src/display.rs) for the
implementation; the highlights worth noting in docs:

1. **The bitstream is a `#[repr(align(4))]` struct, not a bare `[u8; N]`.**
   RP2040 DMA performs word-sized reads and silently masks low address
   bits — a misaligned source produces wandering scan-row artefacts (see
   [`bring-up-notes.md`](./bring-up-notes.md#alignment-bug)). Upstream
   Pimoroni hits this with `alignas(4)`; Rust needs the wrapper struct.

2. **DMA chain is configured via PAC**, not via `embassy_rp::dma`. The
   high-level `Channel` API is one-shot oriented; we need two channels
   that re-arm each other indefinitely. The pattern (matching upstream
   Pimoroni and `kjagiello/hub75-pio-rs/src/dma.rs`):

   - `data` channel: read_addr=0 (set by ctrl), write_addr=PIO TX FIFO,
     trans_count=BITSTREAM_LENGTH/4, treq=`PIO0_TX0`, chain_to=ctrl,
     incr_read=true.
   - `ctrl` channel: read_addr=&BITSTREAM_PTR, write_addr=&data.read_addr
     (the **plain** alias, not `al3_read_addr_trig`), trans_count=1,
     treq=`PERMANENT`, chain_to=data.

   Trigger via `pac::DMA.multi_chan_trigger().write(|w| w.set_multi_chan_trigger(1 << CH_CTRL))`.
   Trans_count is auto-reloaded from a hidden shadow on each chain.

3. **PIO clock divider stays at the embassy-rp default (1.0 = full system
   clock, 125 MHz on RP2040)**. Same as upstream. Anything else gives
   either flicker (too slow) or shift-register data corruption (too
   fast).

4. **`out pins, 8` with `out_count=4`** — the upper 4 bits are
   discarded silently. The bitstream stores the row-select byte as 8
   bits of which only the low nibble matters.

5. **Pin handoff order** at init: bit-bang the column-driver config
   register on temporary `Output`s (via `pin.reborrow()` so the original
   `Peri` survives), drop them, hand the same `Peri`s to PIO via
   `Common::make_pio_pin`. There's a brief float window between drop
   and PIO claim, but PIO's `set_pins(Level::High, …)` is called before
   `set_pin_dirs(…)` so pins go straight to their initial level when
   they become outputs.

6. **`set_pin_dirs` and `set_pins`** in embassy-rp 0.10 wrap the PINCTRL
   register modification in a `with_paused` block that saves and
   restores PINCTRL — calling them after `set_config` is safe and
   doesn't clobber sideset/out/set bases.

## What's deferred

| Capability                        | Status | Notes |
|-----------------------------------|--------|-------|
| Solid-fill flags                  | done | every flag value renders cleanly |
| Chequered tile                    | done | 4×4, black/white |
| Pit-lane stripe                   | done | 2-px right edge in pit-blue |
| Blink animation (waved-yellow)    | done | 250 ms toggle |
| Brightness buttons                | done | up / down / sleep-toggle, no persistence |
| `embassy-rp::flash` brightness persistence | deferred | needs long-press detection; one erase per click would block USB CDC for ~25 ms each time |
| `embedded-graphics` `DrawTarget`  | deferred | not needed for v1 — no text/icons in the flag display |
| Audio (I²S / synth)               | out of scope | pin assignments documented in hardware.md |
| Wi-Fi (cyw43)                     | out of scope | SimHub is over USB |
| Light sensor / auto-brightness    | deferred | hardware wired and documented |
| OTA / web UI                      | no | |
| CI                                | deferred | structure supports it |

## What I'd change if I had to bring this up again

1. Wrap the bitstream in `#[repr(align(4))]` from the very first commit,
   not after it goes wrong. (Easy lesson; cost me a debug round.)
2. Pin `pio` and `pio-proc` to whatever embassy-rp depends on at the
   start; the 0.2 → 0.3 cargo error message points at it but doesn't
   say "you need to match embassy-rp's pio version".
3. Skip the GPIO bit-bang smoke test — going straight to PIO worked
   fine once the alignment was fixed. The bit-bang would only have
   helped if the wiring were the bug, which it wasn't.
4. Don't bother with `embedded-graphics` for a flag display. The
   per-pixel `DrawTarget` callback would funnel into our BCM-stamping
   `set_pixel` 1024 times per frame, which is wasteful when all you
   want is a fullscreen colour fill (call `Display::fill` instead).
   `embedded-graphics` becomes worth it only if we add overlays
   (text, icons, scrolling) later.
