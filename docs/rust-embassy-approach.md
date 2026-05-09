# Firmware approach — Rust + embassy on RP2040

Target board: original Cosmic Unicorn (Pico W on board, RP2040). Firmware in Rust,
async runtime: [embassy-rs](https://github.com/embassy-rs/embassy).

## What's in the ecosystem already

### `embassy-rp` — the HAL

Use this as our HAL. It gives us:

- An async executor (`embassy-executor`)
- USB device support (`embassy-rp::usb`) → CDC-ACM via `embassy-usb` for the SimHub link
- PIO bindings (`embassy-rp::pio`)
- DMA helpers (`embassy-rp::dma`)
- GPIO, ADC (for the light sensor), I²C, SPI, etc.

Docs: <https://docs.embassy.dev/embassy-rp/>

### `pio` + `pio-proc` — assemble PIO programs at compile time

The `pio_asm!` macro lets us paste the Cosmic Unicorn `.pio` source nearly verbatim
into a Rust source file and get back a typed `Program`. This is what we'll use to
compile the bitstream PIO program.

### `embedded-graphics`

For drawing flag glyphs / text / icons. We'd implement the
`embedded_graphics::draw_target::DrawTarget` trait on a wrapper around our framebuffer
so we can use the standard `Image`, `Text`, `Rectangle` etc. primitives. This is also
what the existing `hub75-pio-rs` driver does.

### `kjagiello/hub75-pio-rs` — closest existing driver, **does not apply**

This crate is for *standard* HUB75 panels (R1/G1/B1/R2/G2/B2 + ADDR + CLK + LAT + OE).
The Cosmic Unicorn's panel uses a column shift-register topology with serial RGB data
and a 4→16 row decoder; the protocol is incompatible. We can borrow ideas (PIO + DMA
chain pattern, `embedded-graphics` integration, gamma table approach) but not the
crate itself.

### Existing Cosmic Unicorn Rust ports

None found at time of writing. We're building this.

### Other useful crates

- `embassy-usb`, `embassy-usb-driver` — USB stack on top of `embassy-rp::usb`
- `embassy-time` — `Timer::after`, `Duration`, `Instant`
- `defmt` + `defmt-rtt` — logging via the SWD probe (or skip if we want console-free)
- `panic-probe` — panic handler that flushes defmt before halting
- `tinybmp` — load BMP icons for flags from `&'static [u8]`
- `heapless::String`, `heapless::Vec` — fixed-size collections, no allocator needed

## Recommended firmware architecture

```
┌──────────────────────────────────────────────────────────┐
│ embassy executor (single core for v1, dual core later)  │
│                                                          │
│  ┌────────────┐  ┌──────────────┐  ┌──────────────────┐ │
│  │ usb_task   │  │ parser_task  │  │ render_task     │ │
│  │ (CDC RX)   │─►│ (frame→state)│─►│ (state→frame-   │ │
│  │            │  │              │  │  buffer pixels) │ │
│  └────────────┘  └──────────────┘  └──────────────────┘ │
│                                                          │
│  display module: PIO + DMA chain, runs without CPU       │
│                                                          │
│  buttons_task: debounce + brightness/test triggers       │
└──────────────────────────────────────────────────────────┘
```

Channels between tasks: `embassy_sync::channel::Channel` or
`embassy_sync::pubsub` (single producer, multi consumer for state changes).

### Crate layout

```
firmware/
├── Cargo.toml
├── memory.x                    ← linker script for RP2040 flash/RAM split
├── build.rs                    ← copies memory.x into the target
├── .cargo/config.toml          ← runner = probe-rs / elf2uf2
└── src/
    ├── main.rs                 ← spawns tasks, owns peripherals
    ├── display/
    │   ├── mod.rs              ← public Display struct (set_pixel, set_brightness)
    │   ├── pio.rs              ← pio_asm!{} program + PIO/DMA setup
    │   ├── framebuffer.rs      ← bitstream layout, gamma LUT
    │   └── gamma.rs            ← GAMMA_14BIT table, ported from upstream
    ├── protocol.rs             ← SimHub line parser → State struct
    ├── flags.rs                ← FlagKind enum, render_flag(state, &mut display)
    └── ui.rs                   ← splash, idle, error screens
```

### `Cargo.toml` skeleton (illustrative)

```toml
[package]
name = "uniflag-firmware"
version = "0.1.0"
edition = "2021"

[dependencies]
embassy-executor = { version = "0.6", features = ["arch-cortex-m", "executor-thread", "task-arena-size-8192"] }
embassy-rp       = { version = "0.2", features = ["rp2040", "time-driver", "critical-section-impl", "unstable-pac"] }
embassy-time     = { version = "0.3" }
embassy-sync     = { version = "0.6" }
embassy-usb      = { version = "0.3" }
embassy-futures  = { version = "0.1" }

cortex-m         = "0.7"
cortex-m-rt      = "0.7"
panic-probe      = { version = "0.3", features = ["print-defmt"] }
defmt            = "0.3"
defmt-rtt        = "0.4"

pio              = "0.2"
pio-proc         = "0.2"
fixed            = "1"
heapless         = "0.8"
embedded-graphics = "0.8"
```

### Pin acquisition (sketch)

```rust
let p = embassy_rp::init(Default::default());

// Hold row decoder pins HIGH so the panel doesn't flash row 0 garbage at boot.
let _row0 = Output::new(p.PIN_17, Level::High);
let _row1 = Output::new(p.PIN_18, Level::High);
let _row2 = Output::new(p.PIN_19, Level::High);
let _row3 = Output::new(p.PIN_20, Level::High);

// Hand the panel pins off to the PIO display driver.
let display = Display::new(
    p.PIO0,                 // pio block
    p.DMA_CH0, p.DMA_CH1,   // data + control DMA channels
    DisplayPins {
        column_clock: p.PIN_13,
        column_data:  p.PIN_14,
        column_latch: p.PIN_15,
        column_blank: p.PIN_16,
        row_bit_0:    p.PIN_17,
        row_bit_1:    p.PIN_18,
        row_bit_2:    p.PIN_19,
        row_bit_3:    p.PIN_20,
    },
);

// User buttons (active low, internal pull-up).
let btn_a = Input::new(p.PIN_0, Pull::Up);
// … etc
```

### USB CDC + parser (sketch)

```rust
#[embassy_executor::task]
async fn usb_task(usb: Usb<'static, USB>) -> ! {
    let mut state = embassy_usb::Builder::new(/* ... */).build();
    let mut cdc = CdcAcmClass::new(/* ... */);
    join(state.run(), cdc_loop(cdc, /* line_tx */)).await;
}

#[embassy_executor::task]
async fn parser_task(rx: Channel<'static, ThreadModeRawMutex, [u8; 64], 4>) -> ! {
    loop {
        let line = rx.receive().await;
        if let Ok(state) = parse_line(&line) {
            STATE.signal(state);   // embassy_sync::signal::Signal
        }
    }
}

#[embassy_executor::task]
async fn render_task(mut display: Display<'static>) -> ! {
    let mut current = State::default();
    loop {
        match select(STATE.wait(), Timer::after(Duration::from_millis(50))).await {
            Either::First(new) => current = new,
            Either::Second(_)   => {}    // 20 Hz tick to drive blink animations
        }
        render(&current, &mut display);
    }
}
```

## Step-by-step build plan

1. **Bootstrap.** Cargo new, add embassy deps, get a "blink the on-board LED" example
   running. Confirm probe-rs/elf2uf2 flashing works on the actual hardware.
2. **GPIO smoke test.** Drive a single column of LEDs by hand (no PIO, just bit-bang
   with timer waits) — proves the column shift register chain is wired the way the
   docs say.
3. **PIO program.** Translate `cosmic_unicorn.pio` to a `pio_asm!{}` block. Allocate
   one PIO block and one state machine. Run it with a hand-built static bitstream
   (single solid colour) and verify the panel lights up correctly.
4. **DMA chain.** Add the self-chaining DMA pair to drive the PIO without CPU.
   Confirm it free-runs.
5. **Framebuffer + gamma.** Port `GAMMA_14BIT` and `set_pixel(x, y, r, g, b)`. Write a
   spinning gradient demo.
6. **embedded-graphics.** Implement `DrawTarget` on the display. Render text and a
   simple flag image.
7. **USB CDC.** Add `embassy-usb` with a CDC-ACM class. Echo lines to confirm SimHub
   sees the device.
8. **Parser.** Define the line format ([`simhub-custom-serial.md`](./simhub-custom-serial.md))
   and parse it into a `State`.
9. **Flag renderer.** Map state → screen content (block colour, icon, blink).
10. **Buttons.** Brightness up/down, test patterns, mute / sleep behaviour.
11. **Splash + idle screens** for "no SimHub connection" and "game in menu".
12. **Polish.** Persist brightness across resets (use the last sector of flash, see
    `embassy-rp`'s `flash` module).

## Open questions / defer-til-later

- **PIO clock divider.** The upstream `.cpp` sets it; copy that value. Anything else
  will give the wrong refresh rate or shift-register timing.
- **Which PIO instance.** Audio uses one SM on its own PIO. Display uses one SM. We
  can share a PIO block if pin counts allow, but keeping the display on `PIO0` and
  reserving `PIO1` for future audio is cleaner.
- **Wi-Fi / Pico W.** The Pico W's Wi-Fi chip (`cyw43`) lives on SPI behind the on-board
  CYW43 driver. We don't need it for v1; SimHub is over USB. Mention it for completeness;
  could be useful later for OTA / a "phone shows what flag is shown" companion.
- **Dual core.** Embassy supports running tasks on both cores. Probably overkill for v1
  — render is cheap, USB is async. Worth it only if we add audio and want hard-real-time
  rendering on one core.
- **Flash size / framebuffer.** Bitstream size: per scan row × 14 BCD frames × 16 rows ×
  ~72 bytes ≈ **16 KB**. RP2040 has 264 KB SRAM, plenty of headroom. Double buffering
  doubles it to ~32 KB; still fine.
- **No-std `alloc`?** Avoid. Use `heapless` and statics.

## Sources

- [embassy-rs](https://github.com/embassy-rs/embassy) and [`embassy-rp` docs](https://docs.embassy.dev/embassy-rp/)
- [`pio` crate](https://docs.rs/pio/) and [`pio-proc`](https://docs.rs/pio-proc/)
- [`kjagiello/hub75-pio-rs`](https://github.com/kjagiello/hub75-pio-rs) — reference architecture, not directly usable
- Pimoroni Pico SDK [`cosmic_unicorn`](https://github.com/pimoroni/pimoroni-pico/tree/main/libraries/cosmic_unicorn)
