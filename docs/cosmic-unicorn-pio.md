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

## Reproducing in Rust

The Rust ecosystem has two main paths for PIO programs:

1. **`pio` + `pio-proc::pio_asm!`** (rp-rs / embassy-rp): write the PIO assembly inline
   in a Rust macro and let the proc-macro assemble it at compile time. This is the most
   direct port — you copy the program above (with the `.side_set` / `.wrap_target`
   directives) almost verbatim into a `pio_asm!{ ... }` block.
2. **`pio` crate's `Assembler` builder API**: emit instructions programmatically. More
   verbose, used when you need to build PIO programs at runtime.

Embassy uses the `embassy-rp` HAL which wraps `rp2040-hal`'s PIO abstractions. For
this project go with **option 1** — `pio_asm!` keeps the assembly readable next to
the comments above. See [`rust-embassy-approach.md`](./rust-embassy-approach.md) for
the full firmware sketch.

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

## What the upstream C++ side does at init

For 1:1 behavioural parity:

1. Set `ROW_BIT_0..3` HIGH on every GPIO before enabling the SM (avoids a flash of
   "row 0" garbage during init).
2. Build the bitstream once — fill all the `pixel-count`, `row-select`, padding, and
   per-frame BCD tick fields. Pixel data starts as zeros; only those 64-byte regions
   change at runtime.
3. Initialise the PIO program at a free offset, set sideset/out/set pin counts, set
   the autopull threshold to 32, and start the SM.
4. Configure two DMA channels: a **data** channel pulling 32-bit words from the
   bitstream into the PIO TX FIFO, and a **control** channel that re-arms the data
   channel's read pointer. Start them.
