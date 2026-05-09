# Bring-up notes

A log of the non-obvious things that bit during the v1 bring-up, kept
here so future-me (or anyone porting this to another panel) can avoid
the same potholes.

## Alignment bug

**Symptom**: panel shows fragments of dim colour at wandering y-positions.
Bright "lines" of pixels appear at random scan rows, with seemingly
random colours, even when the firmware should be painting solid black
or a pure 1-px white border.

**Cause**: the bitstream array was `static BITSTREAM: ConstStaticCell<[u8; 16128]>`.
Rust's `[u8; N]` has alignment 1. The RP2040 DMA performs word-sized
reads on the AHB bus, and AHB silently masks low address bits when the
source isn't 4-aligned. Result: every 32-bit DMA read starts ~3 bytes
earlier than intended. The PIO then interprets:

- pixel-data bytes as the row-select byte
- BCD-tick bytes as pixel data
- pixel-count bytes as random PIO instructions

…which produces exactly the "fragmented colours at random rows"
pattern observed.

**Fix**: wrap the bitstream in a `#[repr(align(4))]` struct.

```rust
#[repr(align(4))]
struct Bitstream([u8; BITSTREAM_LENGTH]);

static BITSTREAM: ConstStaticCell<Bitstream> =
    ConstStaticCell::new(Bitstream([0u8; BITSTREAM_LENGTH]));
```

Upstream Pimoroni hits the same constraint with `alignas(4) uint8_t bitstream[…]`.
Pico C SDK's DMA helpers don't enforce alignment either — it's purely
a programmer obligation.

**Detection lesson**: a `debug_assert_eq!(addr & 0b11, 0, …)` on the
bitstream pointer at init catches this in seconds. We added one after
the fact in `display.rs`.

## DMA chain pattern

We have two DMA channels in a self-feeding pair: `data` pushes the
bitstream into PIO's TX FIFO, `ctrl` resets `data`'s read pointer
once `data` has drained. The first attempt used `al3_read_addr_trig`
on `ctrl`'s write_addr — the trigger alias atomically writes the
address AND restarts the channel. Combined with `chain_to=DATA` on
`ctrl`, that creates a queued chain trigger that fires after `data`
completes, racing with `ctrl`'s own re-arm and producing additional
visible artefacts.

**Working pattern** (matches upstream Pimoroni and
`kjagiello/hub75-pio-rs/src/dma.rs`):

- `ctrl.write_addr` = `&data.read_addr` (the **plain** register, no trigger)
- `ctrl.chain_to` = `DATA` — chain restarts data
- `data.chain_to` = `CTRL` — chain restarts ctrl
- one and only one trigger per cycle

The atomic-trigger alias works for a one-shot but doesn't compose with
chain_to. Pick one or the other; we picked chain_to.

## RP2040 + Rust gotchas worth remembering

- **`[u8; N]` is not 4-byte aligned**. `#[repr(align(4))]` wrapper or
  `[u32; N/4]` directly.
- **Cortex-M0+ has no atomic CAS**. `portable-atomic` with the
  `critical-section` feature provides the polyfill that
  `static_cell` / `embassy-sync` need.
- **`pio` and `pio-proc` versions must match embassy-rp's**. embassy-rp
  0.10 uses `pio = "0.3"`; if you use `pio = "0.2"` the
  `Program<32>` type from the macro doesn't unify with
  `Common::load_program(&Program<SIZE>)`.
- **`pio::pio_asm!`, not `pio_proc::pio_asm!`**. The proc-macro's
  user-facing entry point is wrapped by a macro_rules in `pio` 0.3.
- **`#[embassy_executor::task]` returns `Result<SpawnToken, _>`**, not
  `SpawnToken` directly. `.expect("spawn name")` it.
- **`StateMachine::tx_fifo_ptr()`** is on the SM, not on `sm.tx()`.
- **embassy-rp 0.10 has no high-level helper for self-chaining DMA**. Drop
  to `embassy_rp::pac::DMA` and write CtrlTrig values directly. About
  ~30 lines.
- **The Pico W onboard LED is on the cyw43 chip**, not a GPIO. A naive
  "blink to verify firmware is running" smoke test fails before
  anything is wrong. Use the panel itself or a probe-rs `defmt::info!`
  RTT instead.

## Embassy-rp PIO API recap

The pattern that ended up working:

```rust
let pio = Pio::new(p.PIO0, irqs);
let Pio { mut common, mut sm0, .. } = pio;

let clock_pin = common.make_pio_pin(p.PIN_13);
let data_pin = common.make_pio_pin(p.PIN_14);
// ... etc

let prg = pio::pio_asm!(/* multi-line strings */);
let loaded = common.load_program(&prg.program);

let mut cfg = Config::default();
cfg.use_program(&loaded, &[&clock_pin]);   // 2nd arg is sideset pins
cfg.set_set_pins(&[&data_pin, &latch_pin, &blank_pin]);  // consecutive!
cfg.set_out_pins(&[&row0, &row1, &row2, &row3]);          // consecutive!
cfg.shift_out = ShiftConfig {
    threshold: 32,
    direction: ShiftDirection::Right,
    auto_fill: true,                       // auto-pull
};
cfg.fifo_join = FifoJoin::TxOnly;          // 8-deep TX FIFO

sm0.set_config(&cfg);
sm0.set_pins(Level::High, &[&blank, &row0..&row3]);  // levels first
sm0.set_pin_dirs(Direction::Out, &[…all 8 pins…]);   // then dirs
// (set_pins / set_pin_dirs save+restore PINCTRL via with_paused)

setup_dma_chain(sm0.tx_fifo_ptr() as u32);   // PAC writes
sm0.set_enable(true);
start_dma_chain();                            // multi_chan_trigger
```

## Diagnostic checkpoints worth keeping

When something looks wrong, the fastest first checks:

1. `defmt::info!` on the bitstream pointer at init.
   `debug_assert_eq!(addr & 0b11, 0)` — alignment.
2. PIO refresh-rate counter: add `irq 0 set` at `.wrap_target` in the
   PIO program and a counter task that polls it at 1 Hz. The Cosmic
   Unicorn refreshes at ~300 fps, so the counter should advance by
   ~300 per second. Dead silence → the SM stalled (FIFO empty? bad
   config?). Reasonable rate but bad picture → encoding bug.
3. `pac::DMA.ch(0).al1_ctrl().read().busy()` polled — should always
   be high while the panel is meant to be refreshing. False → the
   chain broke.

We didn't end up wiring all of these in for v1, but they're trivial to
add if a future change regresses.
