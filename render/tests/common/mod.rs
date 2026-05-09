//! Shared test helpers. Lives under `tests/` so it doesn't get compiled
//! into the library — this is where `alloc::String` is allowed.

use uniflag_render::{Rgb, Surface, HEIGHT, WIDTH};

/// In-memory [`Surface`] for host tests. Holds a 32×32 array and exposes
/// helpers (`pixel`, `count_lit`, `render_text`) for snapshot assertions.
#[derive(Clone)]
pub struct MockSurface {
    pixels: [[Rgb; WIDTH]; HEIGHT],
}

impl Default for MockSurface {
    fn default() -> Self {
        Self::new()
    }
}

#[allow(dead_code)] // each integration-test file uses a different subset
impl MockSurface {
    pub const fn new() -> Self {
        Self {
            pixels: [[(0, 0, 0); WIDTH]; HEIGHT],
        }
    }

    /// Read a pixel. Out-of-range coords return black (matches the
    /// silently-ignored writes).
    pub fn pixel(&self, x: i32, y: i32) -> Rgb {
        if x < 0 || y < 0 || x >= WIDTH as i32 || y >= HEIGHT as i32 {
            return (0, 0, 0);
        }
        self.pixels[y as usize][x as usize]
    }

    /// Count pixels with any non-zero channel.
    pub fn count_lit(&self) -> usize {
        self.pixels
            .iter()
            .flatten()
            .filter(|&&(r, g, b)| r > 0 || g > 0 || b > 0)
            .count()
    }

    /// Count pixels whose dominant channel is red (i.e. r > g and r > b).
    pub fn count_redish(&self) -> usize {
        self.pixels
            .iter()
            .flatten()
            .filter(|&&(r, g, b)| r > g && r > b && r > 0)
            .count()
    }

    /// Render the panel as a 32-row ASCII grid. Each pixel is one
    /// character: dominant colour as a letter, uppercase if bright,
    /// lowercase if dim, space for fully black. Stable enough for `insta`
    /// snapshot comparison and reviewable in `git diff`.
    pub fn render_text(&self) -> String {
        let mut out = String::with_capacity((WIDTH + 1) * HEIGHT);
        for row in &self.pixels {
            for &px in row {
                out.push(render_char(px));
            }
            out.push('\n');
        }
        out
    }
}

impl Surface for MockSurface {
    fn set_pixel(&mut self, x: i32, y: i32, color: Rgb) {
        if x < 0 || y < 0 || x >= WIDTH as i32 || y >= HEIGHT as i32 {
            return;
        }
        self.pixels[y as usize][x as usize] = color;
    }
}

fn render_char((r, g, b): Rgb) -> char {
    let max = r.max(g).max(b);
    if max == 0 {
        return ' ';
    }
    let bright = max >= 128;
    let threshold = (max / 2).max(24);
    let r_on = r >= threshold;
    let g_on = g >= threshold;
    let b_on = b >= threshold;
    let ch = match (r_on, g_on, b_on) {
        (true, true, true) => 'W',
        (true, true, false) => 'Y',
        (true, false, true) => 'M',
        (false, true, true) => 'C',
        (true, false, false) => 'R',
        (false, true, false) => 'G',
        (false, false, true) => 'B',
        (false, false, false) => '.',
    };
    if bright {
        ch
    } else {
        ch.to_ascii_lowercase()
    }
}
