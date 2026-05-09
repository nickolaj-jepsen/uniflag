//! [`Surface`] is the small abstraction the renderer paints into. The
//! firmware implements it for the hardware `Display`; host tests provide
//! their own `MockSurface` (see `tests/common/mod.rs`).

use crate::{HEIGHT, WIDTH};

/// 8-bit-per-channel RGB colour. Matches the hardware's panel bit-depth
/// before gamma; it's also what the `effects` paint functions return at
/// every pixel.
pub type Rgb = (u8, u8, u8);

/// A 32×32 pixel sink. Implementors only need to provide [`set_pixel`];
/// [`fill`] and [`fill_with`] have default implementations in terms of it.
///
/// Out-of-range coordinates are silently ignored — the firmware
/// `Display` does the same, and effects sometimes paint past the edges
/// (e.g. the green-flag sweep band).
pub trait Surface {
    /// Stamp a single pixel. `x` is `0..WIDTH`, `y` is `0..HEIGHT`. Both
    /// are `i32` because effects do arithmetic that briefly goes negative.
    fn set_pixel(&mut self, x: i32, y: i32, color: Rgb);

    /// Fill the entire panel with one colour.
    fn fill(&mut self, color: Rgb) {
        for y in 0..HEIGHT as i32 {
            for x in 0..WIDTH as i32 {
                self.set_pixel(x, y, color);
            }
        }
    }

    /// Set every pixel from a closure of `(x, y) -> Rgb`. Replaces the
    /// `for y { for x { … set_pixel } }` boilerplate that recurs across
    /// the per-flag effects.
    fn fill_with<F>(&mut self, mut f: F)
    where
        F: FnMut(i32, i32) -> Rgb,
        Self: Sized,
    {
        for y in 0..HEIGHT as i32 {
            for x in 0..WIDTH as i32 {
                self.set_pixel(x, y, f(x, y));
            }
        }
    }
}
