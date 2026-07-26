//! Firmware-local screens: the fallback "roaming ember" and the
//! button-test pattern.
//!
//! These are the only pictures the device paints for itself. Everything
//! else on the panel is a frame streamed from the host — the firmware
//! renders no effects and links no render code.
//!
//! - The fallback screen implements docs/flag-grammar.md §7 state (a)
//!   **bit-exactly**: LUT-free integer math, with the ember hue and the
//!   brightness-scale idiom embedded as literals/locals. The grammar
//!   forbids deriving them from shared render code, and this crate keeps
//!   that true by construction: it is firmware-only and is never linked
//!   by the plugin renderer.
//! - The 3×5 font below reuses the plugin's glyph row-bitmap *format* but
//!   none of its data: the plugin's 7×11 glyphs are far too wide to fit a
//!   version string on a 32-px panel.
//!
//! Painting goes through [`Canvas`] rather than the firmware's `Display`
//! so this crate builds — and is tested — on the host. The firmware's
//! `Display` implements the trait; [`Buffer`] is the in-memory
//! implementation the tests paint into.

#![no_std]

use proto::packet::FRAME_PAYLOAD_LEN;

// Re-exported so callers (and tests) get the panel geometry from the same
// place as the paint routines that assume it.
pub use proto::packet::{PANEL_HEIGHT, PANEL_WIDTH};

/// RGB triple.
pub type Rgb = (u8, u8, u8);

/// A 32×32 paint target. Implementations must silently ignore
/// out-of-range coordinates: the screens below clip by writing past the
/// edges rather than by branching.
pub trait Canvas {
    fn set_pixel(&mut self, x: i32, y: i32, r: u8, g: u8, b: u8);
}

/// In-memory [`Canvas`]: RGB888, row-major, pixel `(x, y)` at byte offset
/// `(y * 32 + x) * 3` — the same layout as a Frame packet's payload.
pub struct Buffer {
    pixels: [u8; FRAME_PAYLOAD_LEN],
}

impl Buffer {
    pub const fn new() -> Self {
        Self {
            pixels: [0; FRAME_PAYLOAD_LEN],
        }
    }

    /// The raw frame bytes.
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }

    /// The colour at `(x, y)`. Panics out of range — tests want the panic.
    pub fn pixel(&self, x: usize, y: usize) -> Rgb {
        let i = (y * PANEL_WIDTH + x) * 3;
        (self.pixels[i], self.pixels[i + 1], self.pixels[i + 2])
    }

    /// True when every channel of every pixel is zero.
    pub fn is_black(&self) -> bool {
        self.pixels.iter().all(|&b| b == 0)
    }
}

impl Default for Buffer {
    fn default() -> Self {
        Self::new()
    }
}

impl Canvas for Buffer {
    fn set_pixel(&mut self, x: i32, y: i32, r: u8, g: u8, b: u8) {
        if !(0..PANEL_WIDTH as i32).contains(&x) || !(0..PANEL_HEIGHT as i32).contains(&y) {
            return;
        }
        let i = (y as usize * PANEL_WIDTH + x as usize) * 3;
        self.pixels[i] = r;
        self.pixels[i + 1] = g;
        self.pixels[i + 2] = b;
    }
}

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

/// Frames in one full round trip of the ember (8 s at 60 fps).
pub const EMBER_PERIOD: u32 = 480;

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
pub fn paint_fallback<C: Canvas>(d: &mut C, frame: u32) {
    fill(d, BLACK);

    let t = frame % EMBER_PERIOD;
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
pub fn paint_test<C: Canvas>(d: &mut C, fw_version: &str) {
    fill(d, BLACK);

    let (w, h) = (PANEL_WIDTH as i32, PANEL_HEIGHT as i32);
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

/// Fill the whole panel with one colour.
fn fill<C: Canvas>(d: &mut C, c: Rgb) {
    rect(d, 0, 0, PANEL_WIDTH as i32, PANEL_HEIGHT as i32, c);
}

/// Axis-aligned filled rectangle. Out-of-range pixels are clipped by
/// `set_pixel`'s bounds check.
fn rect<C: Canvas>(d: &mut C, x: i32, y: i32, w: i32, h: i32, c: Rgb) {
    for yy in y..y + h {
        for xx in x..x + w {
            d.set_pixel(xx, yy, c.0, c.1, c.2);
        }
    }
}

/// Single pixel.
fn dot<C: Canvas>(d: &mut C, x: i32, y: i32, c: Rgb) {
    d.set_pixel(x, y, c.0, c.1, c.2);
}

// Minimal pixel font (digits, 'v', '.'): one byte per glyph row, leftmost
// column on the MSB side — so bit 2 is column 0 for the 3-wide glyphs.

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
pub fn draw_text<C: Canvas>(d: &mut C, mut x: i32, y: i32, text: &str, c: Rgb) -> i32 {
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
