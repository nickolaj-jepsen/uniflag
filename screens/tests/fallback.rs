//! Behavioural conformance for the roaming-ember fallback
//! (docs/flag-grammar.md §7 state (a)).
//!
//! These assert the properties the spec actually promises — period,
//! symmetry, travel, containment, constant luminance, peripheral silence —
//! rather than pinning frame bytes. The ember is allowed to be retuned;
//! it is not allowed to blink, wander off its margin, or get bright.
//!
//! Convenient exactness: the ember's red channel is 255, and
//! `(255 * (m + 1)) >> 8 == m` for every `m <= 255`, so a pixel's red value
//! *is* its brightness multiplier. The luminance assertions read multipliers
//! straight off the frame.

use screens::{paint_fallback, Buffer, EMBER_PERIOD, PANEL_HEIGHT, PANEL_WIDTH};

/// Rows the ember is allowed to touch (core, then halo above).
const CORE_ROW: usize = 30;
const HALO_ROW: usize = 29;
/// Columns it is allowed to touch: x0 spans 12..=19, cells span x0-1..=x0+2.
const MIN_COL: usize = 11;
const MAX_COL: usize = 21;

fn frame_at(f: u32) -> Buffer {
    let mut b = Buffer::new();
    paint_fallback(&mut b, f);
    b
}

/// Sum of the red channel across one row — i.e. the row's total brightness.
fn row_brightness(b: &Buffer, y: usize) -> u32 {
    (0..PANEL_WIDTH).map(|x| b.pixel(x, y).0 as u32).sum()
}

#[test]
fn cycle_is_eight_seconds() {
    for f in 0..EMBER_PERIOD {
        assert_eq!(
            frame_at(f).pixels(),
            frame_at(f + EMBER_PERIOD).pixels(),
            "frame {f} differs from frame {} one period later",
            f + EMBER_PERIOD
        );
    }
}

#[test]
fn motion_is_a_symmetric_triangle() {
    for f in 0..EMBER_PERIOD {
        let mirror = EMBER_PERIOD - 1 - f;
        assert_eq!(
            frame_at(f).pixels(),
            frame_at(mirror).pixels(),
            "frame {f} is not the mirror of frame {mirror}"
        );
    }
}

#[test]
fn travels_from_column_12_to_column_19() {
    // The doc promises a round trip between columns 12.0 and 19.0. At both
    // turning points the crossfade sits exactly on a cell, so the peak is
    // unambiguous.
    let start = frame_at(0);
    assert_eq!(
        start.pixel(12, CORE_ROW).0,
        24,
        "cold start is not at column 12"
    );

    let far = frame_at(239);
    assert_eq!(
        far.pixel(19, CORE_ROW).0,
        24,
        "far turn is not at column 19"
    );
}

#[test]
fn stays_inside_the_bottom_margin() {
    for f in 0..EMBER_PERIOD {
        let b = frame_at(f);
        for y in 0..PANEL_HEIGHT {
            for x in 0..PANEL_WIDTH {
                let (r, g, bl) = b.pixel(x, y);
                if (r, g, bl) == (0, 0, 0) {
                    continue;
                }
                assert!(
                    (y == CORE_ROW || y == HALO_ROW) && (MIN_COL..=MAX_COL).contains(&x),
                    "frame {f} lit ({x}, {y}), outside the rows {HALO_ROW}-{CORE_ROW} \
                     × columns {MIN_COL}-{MAX_COL} margin"
                );
            }
        }
    }
}

#[test]
fn total_luminance_is_constant() {
    // Core row: the four crossfade cells sum to 9 + 24 + 9 == 42 exactly
    // before truncation, and each of the four divisions can shed at most 1.
    // Halo row: 6, from two cells.
    for f in 0..EMBER_PERIOD {
        let b = frame_at(f);

        let core = row_brightness(&b, CORE_ROW);
        assert!(
            (39..=42).contains(&core),
            "frame {f}: core row brightness {core} left the 39..=42 band — the \
             crossfade no longer conserves luminance"
        );

        let halo = row_brightness(&b, HALO_ROW);
        assert!(
            (4..=6).contains(&halo),
            "frame {f}: halo row brightness {halo} left the 4..=6 band"
        );
    }
}

#[test]
fn never_blinks() {
    for f in 0..EMBER_PERIOD {
        assert!(
            !frame_at(f).is_black(),
            "frame {f} went dark — the ember blinked"
        );
    }
}

#[test]
fn stays_peripherally_dim() {
    // §7: "the peak channel is 24/255". A fallback screen that gets bright
    // competes with the flag signals it is supposed to defer to.
    for f in 0..EMBER_PERIOD {
        let b = frame_at(f);
        for y in 0..PANEL_HEIGHT {
            for x in 0..PANEL_WIDTH {
                let (r, g, bl) = b.pixel(x, y);
                assert!(
                    r <= 24 && g <= 24 && bl <= 24,
                    "frame {f}: pixel ({x}, {y}) = ({r}, {g}, {bl}) exceeds the 24/255 ceiling"
                );
            }
        }
    }
}

#[test]
fn every_lit_pixel_is_amber() {
    // Amber is the point: a hue absent from the flag vocabulary, so the
    // fallback can never be mistaken for a signal. R > G > B on every pixel.
    for f in 0..EMBER_PERIOD {
        let b = frame_at(f);
        for x in MIN_COL..=MAX_COL {
            for y in [HALO_ROW, CORE_ROW] {
                let (r, g, bl) = b.pixel(x, y);
                if (r, g, bl) == (0, 0, 0) {
                    continue;
                }
                assert!(
                    r > g && g >= bl,
                    "frame {f}: pixel ({x}, {y}) = ({r}, {g}, {bl}) is not amber"
                );
            }
        }
    }
}
