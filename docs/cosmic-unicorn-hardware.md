# Cosmic Unicorn — hardware reference

**Original revision (Pico W / RP2040).** All facts below come from the Pimoroni Pico SDK
sources at [`libraries/cosmic_unicorn/`](https://github.com/pimoroni/pimoroni-pico/tree/main/libraries/cosmic_unicorn)
(`cosmic_unicorn.hpp`, `cosmic_unicorn.cpp`, `cosmic_unicorn.pio`).

## Display panel — *this is not HUB75*

Important: the Cosmic Unicorn is **not** a generic HUB75 panel. It's a custom column-shift-register
design driven by Pimoroni's own bitstream protocol. Generic HUB75 drivers
(e.g. `hub75-pio-rs`) **will not work** without rewriting them.

- **Resolution:** 32 × 32 RGB LEDs (1024 LEDs)
- **Multiplexing:** 1:16 row scan. Each "scan row" drives the top half (rows 0–15) and
  bottom half (rows 16–31) simultaneously, so the panel exposes two 32-pixel column shift
  chains at once → 64 pixels of serial data per scan row.
- **Refresh rate:** ≈ 300 fps with 14-bit colour (BCM, 14 BCD frames per scan row × 16 scan rows)
- **Colour:** 14-bit per channel after gamma correction
  (`GAMMA_14BIT[]` LUT maps 8-bit input → 14-bit output)
- **Brightness:** software-only, applied as `(channel * brightness) >> 8` before gamma

### Column path (per scan row)

```
RP2040 ─┬── COLUMN_DATA ──► serial-in of column shift register chain
        ├── COLUMN_CLOCK ─► shift-register clock (sideset in PIO)
        ├── COLUMN_LATCH ─► latch shifted bits onto outputs
        └── COLUMN_BLANK ─► output enable (active-low blank)

ROW_BIT_0..3 ─► 4-to-16 row decoder selecting one of 16 scan-row pairs
```

Per pixel the panel shifts in three serial bits in BGR order
(see `cosmic-unicorn-pio.md` for the exact PIO instructions).

## GPIO map

| Function           | GPIO | Notes |
|--------------------|------|-------|
| `COLUMN_CLOCK`     | 13   | PIO sideset, drives shift-register CLK |
| `COLUMN_DATA`      | 14   | PIO `set` pin, serial bit out |
| `COLUMN_LATCH`     | 15   | PIO `set` pin, latches column outputs |
| `COLUMN_BLANK`     | 16   | PIO `set` pin, output-enable (active when LOW) |
| `ROW_BIT_0`        | 17   | PIO `out` pin, row-decoder LSB |
| `ROW_BIT_1`        | 18   | PIO `out` pin |
| `ROW_BIT_2`        | 19   | PIO `out` pin |
| `ROW_BIT_3`        | 20   | PIO `out` pin, row-decoder MSB |
| `I2S_DATA`         | 9    | onboard amplifier — second PIO state machine |
| `I2S_BCLK`         | 10   | |
| `I2S_LRCLK`        | 11   | |
| `MUTE`             | 22   | active-low mute on the amplifier |
| `I2C_SDA`          | 4    | both Qw/ST connectors share this bus |
| `I2C_SCL`          | 5    | |
| `LIGHT_SENSOR`     | 28   | ADC, 0–4095 |
| `SWITCH_A`         | 0    | user button A |
| `SWITCH_B`         | 1    | user button B |
| `SWITCH_C`         | 3    | user button C |
| `SWITCH_D`         | 6    | user button D |
| `SWITCH_VOLUME_UP` | 7    | |
| `SWITCH_VOLUME_DOWN` | 8  | |
| `SWITCH_BRIGHTNESS_UP`   | 21 | |
| `SWITCH_BRIGHTNESS_DOWN` | 26 | |
| `SWITCH_SLEEP`     | 27   | |

Buttons are active-low to ground (the upstream library reads them with internal pull-ups).

## Framebuffer / bitstream layout

The PIO consumes a single linear bitstream that contains everything it needs to draw
the whole panel. The layout (per scan row, repeated for all 16 scan rows × 14 BCD frames):

```
For each scan row (0..15):
  For each BCD frame (0..13):
    Byte  0   :  pixel count - 1 (i.e. 64 - 1 = 63), loaded into PIO `Y`
    Byte  1   :  row select bits 0..3 in the low nibble (output to ROW_BIT_0..3)
    Bytes 2..65: 64 pixel words, each `xxxxx bgr` (3 colour bits in low nibble of a byte)
    Bytes 66..67: padding, brings us to dword (4-byte) alignment
    Bytes 68..71: 24-bit BCD tick count (little-endian), determines OE-on duration
                  for this BCD frame — the basis of binary-code-modulation greyscale
```

The buffer is sized so the whole stream fits in RAM and is fed to the PIO by a
**self-chaining DMA pair**:

- A **control** DMA channel writes the start address back into the **data** channel's
  read-pointer register, so the data channel restarts itself on completion.
- The data channel transfers 32-bit words at `BITSTREAM_LENGTH / 4` count straight into
  the PIO TX FIFO. Result: zero CPU overhead while the panel runs.

### Binary Code Modulation (BCM)

For each pixel + colour, the gamma-corrected 14-bit intensity is split across the 14 BCD
frames so frame *n* contributes weight 2ⁿ. The per-frame "tick count" in bytes 68–71 is
correspondingly weighted (frame 0 is shortest, frame 13 longest), giving exponential
brightness control without per-pixel PWM.

## C++ API surface (for reference / behaviour parity)

From `cosmic_unicorn.hpp`:

```cpp
class CosmicUnicorn {
  static const uint16_t WIDTH  = 32;
  static const uint16_t HEIGHT = 32;

  void init();
  void update(PicoGraphics *graphics);
  void clear();
  void set_pixel(int x, int y, uint8_t r, uint8_t g, uint8_t b);

  void  set_brightness(float value);   // 0.0 .. 1.0
  float get_brightness();
  void  adjust_brightness(float delta);

  uint16_t light();                    // 0..4095
  bool     is_pressed(uint8_t button);

  // audio
  void  set_volume(float value);
  float get_volume();
  void  adjust_volume(float delta);
  void  play_sample(uint8_t *data, uint32_t length);
  AudioChannel& synth_channel(uint channel);
  void  play_synth();
  void  stop_playing();
};
```

Button constants: `SWITCH_A`, `SWITCH_B`, `SWITCH_C`, `SWITCH_D`,
`SWITCH_SLEEP`, `SWITCH_VOLUME_UP`, `SWITCH_VOLUME_DOWN`,
`SWITCH_BRIGHTNESS_UP`, `SWITCH_BRIGHTNESS_DOWN`.

## What we don't (yet) need from the upstream library

- **Audio:** the I²S/PIO audio path, synth channels, sample playback. Not relevant for a
  flag display.
- **PicoGraphics:** the C++ drawing helper. We'll use `embedded-graphics` on the Rust side.
- **Sleep / power management:** the upstream library has explicit "off" handling; we
  probably keep the panel running and let the host put the device to sleep.

## What we do need to reproduce in Rust

1. Pin init (set the four ROW_BIT pins HIGH at boot to avoid a flash of garbage).
2. The PIO program (`cosmic_unicorn.pio`) — see [`cosmic-unicorn-pio.md`](./cosmic-unicorn-pio.md).
3. The bitstream framebuffer layout (above).
4. The DMA chain.
5. `set_pixel(x, y, r, g, b)` and `set_brightness(...)` using the gamma-14-bit LUT.
