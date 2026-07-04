//! Firmware-local screens: the fallback "roaming ember" and the
//! button-test pattern.
//!
//! Everything here paints through [`Display::set_pixel`]. The firmware
//! deliberately renders no effects and links no render code — what it
//! needs is embedded here instead:
//!
//! - The fallback screen implements docs/flag-grammar.md §7 state (a)
//!   **bit-exactly**: LUT-free integer math, with the ember hue and the
//!   brightness-scale idiom embedded as literals/locals (the grammar
//!   forbids deriving them from shared render code).
//! - The glyph row-bitmap encoding (one byte per row, MSB side = leftmost
//!   column) follows the plugin's caution-glyph format; at 7×11 those are
//!   far too wide to fit a version string on a 32-px panel — so the 3×5
//!   font below is firmware-original data in that same format, not a copy.

use crate::display::{Display, HEIGHT, WIDTH};

/// RGB triple.
pub type Rgb = (u8, u8, u8);

const BLACK: Rgb = (0, 0, 0);
const WHITE: Rgb = (255, 255, 255);
// Pure single channels — the test screen wants channel separation, not
// the render palette's tuned hues.
const RED: Rgb = (255, 0, 0);
const GREEN: Rgb = (0, 255, 0);
const BLUE: Rgb = (0, 0, 255);

/// Base hue of the fallback ember (docs/flag-grammar.md §7: amber — a hue
/// absent from the flag vocabulary). Brightness comes from the per-pixel
/// multipliers below; the peak channel is 24/255, peripherally silent.
const EMBER_AMBER: Rgb = (255, 120, 8);

// =============================================================================
// Screens
// =============================================================================

/// Scale a colour by an 8-bit brightness multiplier: per channel
/// `(c * (m + 1)) >> 8` — the plugin renderer's `ScaleRgb` idiom, embedded.
fn scale(c: Rgb, m: u32) -> Rgb {
    let s = |ch: u8| ((ch as u32 * (m + 1)) >> 8) as u8;
    (s(c.0), s(c.1), s(c.2))
}

/// Firmware fallback (docs/flag-grammar.md §7 state (a)): device powered,
/// no host stream. Also the boot state before any traffic.
///
/// A single dim amber ember glides along the bottom margin (rows 29–30,
/// columns 11–21, inset off the panel edge): an 8 s triangle round trip
/// between columns 12.0 and 19.0 in 1/16-px steps, rendered as a two-cell
/// linear crossfade with 9-level shoulders and a 6-level halo above — a
/// soft roaming glow ("searching for the host") with constant total
/// luminance, no blink. Distinct from every plugin idle by position, hue
/// and motion class.
pub fn paint_fallback(d: &mut Display, frame: u32) {
    fill(d, BLACK);

    let t = frame % 480;
    let u = if t < 240 { t } else { 479 - t };
    let pos16 = 192 + u * 112 / 239; // column 12.0..=19.0 in 1/16-px units
    let x0 = (pos16 >> 4) as i32;
    let fr = (pos16 & 15) * 17; // exact 0..=255 (15 * 17 == 255)

    // Core row: peak 24 sliding across two cells, shoulders at 9.
    dot(d, x0 - 1, 30, scale(EMBER_AMBER, 9 * (255 - fr) / 255));
    dot(
        d,
        x0,
        30,
        scale(EMBER_AMBER, (24 * (255 - fr) + 9 * fr) / 255),
    );
    dot(
        d,
        x0 + 1,
        30,
        scale(EMBER_AMBER, (9 * (255 - fr) + 24 * fr) / 255),
    );
    dot(d, x0 + 2, 30, scale(EMBER_AMBER, 9 * fr / 255));
    // Halo row above, at 6.
    dot(d, x0, 29, scale(EMBER_AMBER, 6 * (255 - fr) / 255));
    dot(d, x0 + 1, 29, scale(EMBER_AMBER, 6 * fr / 255));
}

/// Local test screen, toggled by a long press: four white corner markers
/// (orientation + extremes), three full-intensity R/G/B bars (per-channel
/// health), and the firmware version — enough to spot dead pixels or a
/// mis-flashed build without any host attached.
pub fn paint_test(d: &mut Display, fw_version: &str) {
    fill(d, BLACK);

    let (w, h) = (WIDTH as i32, HEIGHT as i32);
    for (x, y) in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)] {
        dot(d, x, y, WHITE);
    }

    rect(d, 2, 3, 28, 5, RED);
    rect(d, 2, 10, 28, 5, GREEN);
    rect(d, 2, 17, 28, 5, BLUE);

    // "v" + Cargo package version, e.g. "v0.1.0". Overlong strings clip
    // at the right edge via set_pixel's bounds check.
    let x = draw_text(d, 2, 25, "v", WHITE);
    draw_text(d, x, 25, fw_version, WHITE);
}

// =============================================================================
// Paint helpers
// =============================================================================

/// Fill the whole panel with one colour.
pub fn fill(d: &mut Display, c: Rgb) {
    rect(d, 0, 0, WIDTH as i32, HEIGHT as i32, c);
}

/// Axis-aligned filled rectangle. Out-of-range pixels are clipped by
/// `set_pixel`'s bounds check.
pub fn rect(d: &mut Display, x: i32, y: i32, w: i32, h: i32, c: Rgb) {
    for yy in y..y + h {
        for xx in x..x + w {
            d.set_pixel(xx, yy, c.0, c.1, c.2);
        }
    }
}

/// Single pixel.
pub fn dot(d: &mut Display, x: i32, y: i32, c: Rgb) {
    d.set_pixel(x, y, c.0, c.1, c.2);
}

// =============================================================================
// Minimal pixel font: digits, 'v', '.'
// =============================================================================

// One byte per glyph row, leftmost column on the MSB side — here bit 2 =
// column 0 for the 3-wide glyphs.

const FONT_W: i32 = 3;

#[rustfmt::skip]
const DIGITS: [[u8; 5]; 10] = [
    [0b111, 0b101, 0b101, 0b101, 0b111], // 0
    [0b010, 0b110, 0b010, 0b010, 0b111], // 1
    [0b111, 0b001, 0b111, 0b100, 0b111], // 2
    [0b111, 0b001, 0b111, 0b001, 0b111], // 3
    [0b101, 0b101, 0b111, 0b001, 0b001], // 4
    [0b111, 0b100, 0b111, 0b001, 0b111], // 5
    [0b111, 0b100, 0b111, 0b101, 0b111], // 6
    [0b111, 0b001, 0b001, 0b001, 0b001], // 7
    [0b111, 0b101, 0b111, 0b101, 0b111], // 8
    [0b111, 0b101, 0b111, 0b001, 0b111], // 9
];

#[rustfmt::skip]
const GLYPH_V: [u8; 5] = [
    0b101,
    0b101,
    0b101,
    0b101,
    0b010,
];

/// One column wide; the lit bit sits at bit 2 like every other glyph's
/// leftmost column.
#[rustfmt::skip]
const GLYPH_DOT: [u8; 5] = [
    0b000,
    0b000,
    0b000,
    0b000,
    0b100,
];

/// Render `text` with the glyph top-left at `(x, y)`, 1-px advance gap.
/// Characters outside the font (anything but digits, 'v', '.') are
/// skipped. Returns the x one gap past the last glyph, so calls chain.
pub fn draw_text(d: &mut Display, mut x: i32, y: i32, text: &str, c: Rgb) -> i32 {
    for ch in text.chars() {
        let (rows, width): (&[u8; 5], i32) = match ch {
            '0'..='9' => (&DIGITS[(ch as u8 - b'0') as usize], FONT_W),
            'v' => (&GLYPH_V, FONT_W),
            '.' => (&GLYPH_DOT, 1),
            _ => continue,
        };
        for (row, &bits) in rows.iter().enumerate() {
            for col in 0..width {
                if bits & (1 << (FONT_W - 1 - col)) != 0 {
                    dot(d, x + col, y + row as i32, c);
                }
            }
        }
        x += width + 1;
    }
    x
}
