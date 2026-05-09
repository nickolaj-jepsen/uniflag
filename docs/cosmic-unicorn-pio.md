# Cosmic Unicorn — PIO program

Source: [`pimoroni-pico/libraries/cosmic_unicorn/cosmic_unicorn.pio`](https://github.com/pimoroni/pimoroni-pico/blob/main/libraries/cosmic_unicorn/cosmic_unicorn.pio).

The PIO state machine consumes the bitstream described in
[`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md#framebuffer--bitstream-layout)
and drives the panel directly. The CPU's only role after setup is to occasionally edit
the framebuffer — the panel keeps refreshing itself via DMA → PIO.

## Pin bindings

The C++ side configures the state machine to remap the panel pins onto the SM's
relative pin numbers:

| PIO relative | Cosmic Unicorn signal | Real GPIO |
|--------------|-----------------------|-----------|
| sideset 0    | `COLUMN_CLOCK`        | 13 |
| set 0        | `COLUMN_DATA`         | 14 |
| set 1        | `COLUMN_LATCH`        | 15 |
| set 2        | `COLUMN_BLANK`        | 16 |
| out 0        | `ROW_BIT_0`           | 17 |
| out 1        | `ROW_BIT_1`           | 18 |
| out 2        | `ROW_BIT_2`           | 19 |
| out 3        | `ROW_BIT_3`           | 20 |

`out` shift direction: **right**, autopull at 32-bit threshold. Values are little-endian.

Note that within the program itself the pins read as `0..2` (set / data-latch-blank) and
`3..6` (out / row bits) — see the comment block at the top of the source. The SM's
`set_pins`/`set_sideset_pins`/`set_out_pins` calls map them to the real GPIOs.

## Program (annotated)

```
.wrap_target
  out y, 8                      ; load (pixel-count - 1) into Y → 63
  out pins, 8                   ; pop 8 bits, low 4 = row-select → ROW_BIT_0..3
                                ;  (high 4 bits are wasted, that's fine)

pixels:
    out x, 1       side 0  [1]  ; X = next bit (RED), CLK low, +1 cycle delay
    set pins, 0b100             ; data=0, latch=0, blank=1   (idle data low)
    jmp !x endb                 ; if bit was 0, leave data low
    set pins, 0b101             ; bit was 1 → data=1, blank=1
endb:
    nop            side 1 [2]   ; CLK high, hold 3 cycles → shift register samples

    out x, 1       side 0 [1]   ; X = next bit (GREEN)
    set pins, 0b100
    jmp !x endg
    set pins, 0b101
endg:
    nop            side 1 [2]

    out x, 1       side 0  [1]  ; X = next bit (BLUE)
    set pins, 0b100
    jmp !x endr
    set pins, 0b101
endr:
    out null, 5    side 1 [2]   ; clock high, discard the upper 5 bits of the byte
                                ;  (pixel byte was xxxxxbgr — we used the low 3, dump 5)

  jmp y-- pixels                ; next pixel; y decrements after compare

  out null, 16                  ; discard the 2 padding bytes that pad us to dword
  set pins, 0b110 [5]           ; latch high (latch=1, blank=1, data=0)
  set pins, 0b000               ; blank low → outputs ENABLED for this BCD frame

  out y, 32                     ; pop 32-bit BCD tick count into Y
bcd_delay:
  jmp y-- bcd_delay             ; busy-wait for the configured exposure time

  set pins, 0b100               ; blank high → outputs DISABLED, ready for next row
.wrap
```

## How to read this

- One pass through `.wrap_target ... .wrap` covers **one BCD frame** of **one scan row**.
  A complete refresh is `16 scan rows × 14 BCD frames` of these passes. Fully driven by
  the auto-restarting DMA chain.
- The "pixel" loop shifts in three colour bits per pixel by toggling `data` and pulsing
  `clk` — classic shift-register feeding.
- `blank` is held high (outputs disabled) while shifting and during latch, then dropped
  (outputs enabled) for the duration encoded in the per-frame tick count. That tick count
  doubles between successive BCD frames, which is what gives binary-code-modulation
  greyscale.
- The `[N]` brackets after instructions are PIO delay slots — extra clock cycles inserted
  to give the shift registers enough setup/hold time.

## Required state-machine configuration

The PIO program above only works with this configuration (matching
upstream Pimoroni's setup):

| Register / field        | Value                                                 |
|-------------------------|-------------------------------------------------------|
| `clock_divider`         | 1.0 (full system clock, 125 MHz on RP2040)            |
| `shift_out.direction`   | Right                                                 |
| `shift_out.auto_fill`   | `true` (autopull)                                     |
| `shift_out.threshold`   | 32                                                    |
| `fifo_join`             | TX-only (8-deep TX FIFO)                              |
| `set_pins` base / count | DATA (GPIO 14) / 3                                    |
| `out_pins` base / count | ROW_BIT_0 (GPIO 17) / 4                               |
| `sideset` base / count  | COLUMN_CLOCK (GPIO 13) / 1, optional                  |

`out_count = 4` is intentional even though `out pins, 8` shifts 8 bits —
the upper 4 bits are silently discarded since they fall outside the
configured pin range. The bitstream stores the row select as a full byte
to keep dword alignment.

Without TX-only FIFO join the TX FIFO is only 4 deep, which is
borderline for the bitstream's read pattern — at the BCD-frame boundary
the SM has to consume four words in quick succession (padding +
tick-count + first pixel data of next frame), and with a 4-deep FIFO
there's no slack for DMA latency.

## Embassy-rp integration constraints

If you're driving this from Rust with embassy-rp:

- The `pio` crate version **must match embassy-rp's internal pio dep**.
  Mismatched versions yield a `Program<SIZE>` from the wrong crate that
  does not unify with `Common::load_program(&Program<SIZE>)`.
- Use **`pio::pio_asm!{}`** — `pio_proc::pio_asm!` is re-exported from
  `pio` via a `macro_rules!` wrapper, and the wrapped form is what
  embassy-rp's `Common::load_program` accepts.
- Embassy-rp (as of 0.10) has no high-level helper for self-chaining
  DMA channels. Drop to `embassy_rp::pac::DMA` and write `CtrlTrig`
  values directly (~30 lines). Pattern: `ctrl.write_addr` =
  `&data.read_addr` (the **plain** alias, not `al3_read_addr_trig`),
  `ctrl.chain_to = DATA`, `data.chain_to = CTRL`. The atomic-trigger
  alias works for one-shots but doesn't compose with `chain_to`.

## Quirks worth remembering

- The PIO instruction labels in upstream's `.pio` file (`endb`, `endg`,
  `endr`) and the inline comments (`red bit`, `green bit`, `blue bit`)
  contradict each other. The labels match the per-pixel byte's
  bit-positions (B at bit 0, G at bit 1, R at bit 2 — see
  [`cosmic-unicorn-hardware.md`](./cosmic-unicorn-hardware.md#pixel-byte-format-verified));
  the comments are misleading. Don't reorder the instructions based on
  the comments.
- `out null, 5` between the third colour bit and the next pixel
  discards the unused 5 bits of the pixel byte AND advances the clock
  via its sideset. Replacing it with a plain `nop` (as the first two
  colour bits do) would shift the bit-stream by 5 per pixel — visible
  as a corrupted, slowly-rolling pattern.

## Cycle budget / timing notes

- PIO clock: the upstream library does not pin it down explicitly in the header
  excerpt; check `cosmic_unicorn.cpp` for the divider. The panel's spec is "≈ 300 fps".
  Total cycles per refresh ≈ `(per-pixel cycles × 64 pixels) × 14 BCD frames × 16 rows`
  + the OE delays. Counting from the program: each colour bit costs ~5 cycles (`out`,
  `set`, `jmp`, `nop`+delay) → ~15 cycles/pixel for the shift loop, plus latch/blank
  housekeeping. At a 1 µs PIO cycle that's ~15 ms of *shift* time per refresh; the rest
  is the BCD tick-count delays.
- The state machine intentionally over-pulls and discards bits (the `out null, 5` and
  `out null, 16` instructions) to keep byte alignment in the bitstream simple. Don't
  shorten the framebuffer to "save bytes" — the layout is load-bearing.
