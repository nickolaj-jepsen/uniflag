//! Deterministic test patterns — pure functions of the frame index.
//!
//! Every pattern maps `(pattern, frame_index)` to exactly one 3072-byte
//! RGB888 payload with no other inputs (no wall clock, no randomness), so
//! tests can pin bytes and a given frame index always paints the same
//! panel. The brightness-sweep pattern additionally derives
//! a per-frame [`Packet::Brightness`](proto::packet::Packet::Brightness)
//! value from the same index.

use std::str::FromStr;

use proto::packet::{FRAME_PAYLOAD_LEN, PANEL_HEIGHT, PANEL_WIDTH};

/// One RGB888 colour.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub(crate) struct Rgb {
    pub(crate) r: u8,
    pub(crate) g: u8,
    pub(crate) b: u8,
}

/// Names accepted by `solid:<name>` (hex `solid:RRGGBB` / `solid:#RRGGBB`
/// works for everything else), as `(name, r, g, b)`.
#[rustfmt::skip]
const NAMED_COLORS: &[(&str, u8, u8, u8)] = &[
    ("black",     0,   0,   0),
    ("white",   255, 255, 255),
    ("red",     255,   0,   0),
    ("green",     0, 255,   0),
    ("blue",      0,   0, 255),
    ("yellow",  255, 255,   0),
    ("orange",  255, 128,   0),
    ("cyan",      0, 255, 255),
    ("magenta", 255,   0, 255),
];

/// The checkerboard flips polarity every this many frames (1 s at 30 fps).
const CHECKERBOARD_INVERT_PERIOD: u64 = 30;

/// Frames per full 0..=255 brightness ramp of [`Pattern::BrightnessSweep`].
const BRIGHTNESS_SWEEP_PERIOD: u64 = 256;

/// A test pattern selectable via `--pattern`.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub(crate) enum Pattern {
    /// Every pixel the same colour (`solid:<name>` or `solid:RRGGBB`).
    Solid(Rgb),
    /// Static horizontal red ramp (left → right) plus vertical blue ramp
    /// (top → bottom) — mirrored axes and swapped channels are visually
    /// unambiguous.
    Gradient,
    /// Single white pixel on black walking the panel row-major, one step
    /// per frame, wrapping after the last pixel.
    MovingPixel,
    /// Alternating white/black pixels, inverting polarity every
    /// [`CHECKERBOARD_INVERT_PERIOD`] frames.
    Checkerboard,
    /// Static white frame; [`Pattern::brightness_for_frame`] sweeps a
    /// full 0..=255 brightness ramp alongside it.
    BrightnessSweep,
}

impl Pattern {
    /// Paint frame `frame_index` of this pattern into `pixels` (RGB888,
    /// row-major from the top-left). Pure: identical inputs always yield
    /// identical bytes.
    pub(crate) fn paint(&self, frame_index: u64, pixels: &mut [u8; FRAME_PAYLOAD_LEN]) {
        match *self {
            Pattern::Solid(color) => fill(pixels, color),
            Pattern::Gradient => {
                for_each_pixel(pixels, |x, y| Rgb {
                    r: ramp(x, PANEL_WIDTH),
                    g: 0,
                    b: ramp(y, PANEL_HEIGHT),
                });
            }
            Pattern::MovingPixel => {
                fill(pixels, Rgb { r: 0, g: 0, b: 0 });
                let cursor = (frame_index % (PANEL_WIDTH * PANEL_HEIGHT) as u64) as usize;
                let i = cursor * 3;
                pixels[i] = 0xFF;
                pixels[i + 1] = 0xFF;
                pixels[i + 2] = 0xFF;
            }
            Pattern::Checkerboard => {
                let phase = ((frame_index / CHECKERBOARD_INVERT_PERIOD) % 2) as usize;
                for_each_pixel(pixels, |x, y| {
                    if (x + y + phase).is_multiple_of(2) {
                        Rgb {
                            r: 255,
                            g: 255,
                            b: 255,
                        }
                    } else {
                        Rgb { r: 0, g: 0, b: 0 }
                    }
                });
            }
            Pattern::BrightnessSweep => fill(
                pixels,
                Rgb {
                    r: 255,
                    g: 255,
                    b: 255,
                },
            ),
        }
    }

    /// The Brightness packet value to send alongside frame `frame_index`,
    /// if this pattern exercises the brightness path: the sweep ramps
    /// 0..=255 over [`BRIGHTNESS_SWEEP_PERIOD`] frames, then wraps.
    pub(crate) fn brightness_for_frame(&self, frame_index: u64) -> Option<u8> {
        match self {
            Pattern::BrightnessSweep => Some((frame_index % BRIGHTNESS_SWEEP_PERIOD) as u8),
            _ => None,
        }
    }
}

/// `--pattern` grammar: `solid:<named-or-hex-color>`, `gradient`,
/// `moving-pixel`, `checkerboard`, `brightness-sweep`.
impl FromStr for Pattern {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        if let Some(color) = s.strip_prefix("solid:") {
            return parse_color(color).map(Pattern::Solid);
        }
        match s {
            "gradient" => Ok(Pattern::Gradient),
            "moving-pixel" => Ok(Pattern::MovingPixel),
            "checkerboard" => Ok(Pattern::Checkerboard),
            "brightness-sweep" => Ok(Pattern::BrightnessSweep),
            other => Err(format!(
                "unknown pattern '{other}' (expected solid:<color>, gradient, \
                 moving-pixel, checkerboard, or brightness-sweep)"
            )),
        }
    }
}

fn parse_color(s: &str) -> Result<Rgb, String> {
    if let Some(&(_, r, g, b)) = NAMED_COLORS
        .iter()
        .find(|(name, ..)| name.eq_ignore_ascii_case(s))
    {
        return Ok(Rgb { r, g, b });
    }
    let hex = s.strip_prefix('#').unwrap_or(s);
    if hex.len() == 6 && hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        let byte_at = |i| u8::from_str_radix(&hex[i..i + 2], 16).map_err(|e| e.to_string());
        return Ok(Rgb {
            r: byte_at(0)?,
            g: byte_at(2)?,
            b: byte_at(4)?,
        });
    }
    let names: Vec<&str> = NAMED_COLORS.iter().map(|(name, ..)| *name).collect();
    Err(format!(
        "unknown color '{s}' (expected RRGGBB hex or one of: {})",
        names.join(", ")
    ))
}

fn fill(pixels: &mut [u8; FRAME_PAYLOAD_LEN], color: Rgb) {
    for chunk in pixels.chunks_exact_mut(3) {
        chunk[0] = color.r;
        chunk[1] = color.g;
        chunk[2] = color.b;
    }
}

fn for_each_pixel(pixels: &mut [u8; FRAME_PAYLOAD_LEN], f: impl Fn(usize, usize) -> Rgb) {
    for y in 0..PANEL_HEIGHT {
        for x in 0..PANEL_WIDTH {
            let color = f(x, y);
            let i = (y * PANEL_WIDTH + x) * 3;
            pixels[i] = color.r;
            pixels[i + 1] = color.g;
            pixels[i + 2] = color.b;
        }
    }
}

/// Full-scale linear ramp: position 0 → 0, position `extent - 1` → 255.
fn ramp(position: usize, extent: usize) -> u8 {
    (position * 255 / (extent - 1)) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    const ALL_PATTERNS: &[Pattern] = &[
        Pattern::Solid(Rgb {
            r: 10,
            g: 20,
            b: 30,
        }),
        Pattern::Gradient,
        Pattern::MovingPixel,
        Pattern::Checkerboard,
        Pattern::BrightnessSweep,
    ];

    fn painted(pattern: Pattern, frame_index: u64) -> [u8; FRAME_PAYLOAD_LEN] {
        let mut pixels = [0u8; FRAME_PAYLOAD_LEN];
        pattern.paint(frame_index, &mut pixels);
        pixels
    }

    #[test]
    fn same_pattern_and_frame_index_paint_identical_bytes() {
        for &pattern in ALL_PATTERNS {
            for frame in [0u64, 1, 31, 1023, 1024, u64::MAX] {
                assert_eq!(
                    painted(pattern, frame),
                    painted(pattern, frame),
                    "{pattern:?} frame {frame}"
                );
            }
        }
    }

    #[test]
    fn moving_pixel_consecutive_frames_differ_in_exactly_two_pixels() {
        let a = painted(Pattern::MovingPixel, 0);
        let b = painted(Pattern::MovingPixel, 1);
        let differing_pixels = a
            .chunks_exact(3)
            .zip(b.chunks_exact(3))
            .filter(|(pa, pb)| pa != pb)
            .count();
        assert_eq!(differing_pixels, 2);
        // Frame 0 lights pixel 0; frame 1 lights pixel 1.
        assert_eq!(&a[0..3], &[0xFF, 0xFF, 0xFF]);
        assert_eq!(&a[3..6], &[0, 0, 0]);
        assert_eq!(&b[0..3], &[0, 0, 0]);
        assert_eq!(&b[3..6], &[0xFF, 0xFF, 0xFF]);
    }

    #[test]
    fn moving_pixel_wraps_after_the_last_pixel() {
        let pixels = PANEL_WIDTH * PANEL_HEIGHT;
        assert_eq!(
            painted(Pattern::MovingPixel, 0),
            painted(Pattern::MovingPixel, pixels as u64)
        );
        // The last frame before the wrap lights the bottom-right pixel.
        let last = painted(Pattern::MovingPixel, (pixels - 1) as u64);
        assert_eq!(&last[(pixels - 1) * 3..], &[0xFF, 0xFF, 0xFF]);
    }

    #[test]
    fn gradient_corners_pin_the_orientation() {
        let g = painted(Pattern::Gradient, 0);
        let at = |x: usize, y: usize| {
            let i = (y * PANEL_WIDTH + x) * 3;
            [g[i], g[i + 1], g[i + 2]]
        };
        assert_eq!(at(0, 0), [0, 0, 0], "top-left is black");
        assert_eq!(at(PANEL_WIDTH - 1, 0), [255, 0, 0], "top-right is red");
        assert_eq!(at(0, PANEL_HEIGHT - 1), [0, 0, 255], "bottom-left is blue");
        assert_eq!(
            at(PANEL_WIDTH - 1, PANEL_HEIGHT - 1),
            [255, 0, 255],
            "bottom-right is magenta"
        );
        // Static: the frame index is irrelevant.
        assert_eq!(g, painted(Pattern::Gradient, 12345));
    }

    #[test]
    fn checkerboard_inverts_exactly_at_the_period() {
        let start = painted(Pattern::Checkerboard, 0);
        let just_before = painted(Pattern::Checkerboard, CHECKERBOARD_INVERT_PERIOD - 1);
        let inverted = painted(Pattern::Checkerboard, CHECKERBOARD_INVERT_PERIOD);
        let re_inverted = painted(Pattern::Checkerboard, 2 * CHECKERBOARD_INVERT_PERIOD);
        assert_eq!(start, just_before);
        assert_ne!(start, inverted);
        assert_eq!(start, re_inverted);
        // Every pixel flips between the two phases.
        assert!(start
            .chunks_exact(3)
            .zip(inverted.chunks_exact(3))
            .all(|(a, b)| a != b));
    }

    #[test]
    fn brightness_sweep_covers_the_full_ramp_and_others_send_none() {
        let values: Vec<u8> = (0..BRIGHTNESS_SWEEP_PERIOD)
            .map(|frame| {
                Pattern::BrightnessSweep
                    .brightness_for_frame(frame)
                    .expect("sweep always has a value")
            })
            .collect();
        let expected: Vec<u8> = (0..=255).collect();
        assert_eq!(values, expected);
        // Wraps.
        assert_eq!(
            Pattern::BrightnessSweep.brightness_for_frame(BRIGHTNESS_SWEEP_PERIOD),
            Some(0)
        );
        for pattern in [
            Pattern::Solid(Rgb { r: 1, g: 2, b: 3 }),
            Pattern::Gradient,
            Pattern::MovingPixel,
            Pattern::Checkerboard,
        ] {
            assert_eq!(pattern.brightness_for_frame(7), None, "{pattern:?}");
        }
    }

    #[test]
    fn pattern_grammar_parses() {
        assert_eq!("gradient".parse(), Ok(Pattern::Gradient));
        assert_eq!("moving-pixel".parse(), Ok(Pattern::MovingPixel));
        assert_eq!("checkerboard".parse(), Ok(Pattern::Checkerboard));
        assert_eq!("brightness-sweep".parse(), Ok(Pattern::BrightnessSweep));
        assert_eq!(
            "solid:red".parse(),
            Ok(Pattern::Solid(Rgb { r: 255, g: 0, b: 0 }))
        );
        assert_eq!(
            "solid:Orange".parse(),
            Ok(Pattern::Solid(Rgb {
                r: 255,
                g: 128,
                b: 0
            }))
        );
        assert_eq!(
            "solid:#102030".parse(),
            Ok(Pattern::Solid(Rgb {
                r: 0x10,
                g: 0x20,
                b: 0x30
            }))
        );
        assert_eq!(
            "solid:A1B2C3".parse(),
            Ok(Pattern::Solid(Rgb {
                r: 0xA1,
                g: 0xB2,
                b: 0xC3
            }))
        );
        assert!("solid:notacolor".parse::<Pattern>().is_err());
        assert!("solid:12345".parse::<Pattern>().is_err());
        assert!("plaid".parse::<Pattern>().is_err());
    }
}
