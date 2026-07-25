//! Behavioural conformance for the local test screen and the 3×5 font.
//!
//! The test screen is a diagnostic instrument: it is what the maintainer
//! looks at to decide whether a panel has dead pixels or a bad flash, with
//! no host attached. Its job is to be unambiguous, so these tests pin the
//! things that make it readable — corner extremes, full-intensity channel
//! separation, a legible version string — and the clipping behaviour that
//! keeps a long version from writing off the end of the panel.

use screens::{draw_text, paint_test, Buffer, PANEL_HEIGHT, PANEL_WIDTH};

const VERSION_ROW: usize = 25;
const GLYPH_ROWS: usize = 5;

fn test_screen(version: &str) -> Buffer {
    let mut b = Buffer::new();
    paint_test(&mut b, version);
    b
}

#[test]
fn marks_all_four_corners() {
    let b = test_screen("2.0.0");
    let (w, h) = (PANEL_WIDTH - 1, PANEL_HEIGHT - 1);
    for (x, y) in [(0, 0), (w, 0), (0, h), (w, h)] {
        assert_eq!(
            b.pixel(x, y),
            (255, 255, 255),
            "corner ({x}, {y}) is not a white marker"
        );
    }
}

#[test]
fn paints_three_full_intensity_channel_bars() {
    let b = test_screen("2.0.0");
    // (top row, expected colour) for each bar; each is 5 rows tall,
    // spanning columns 2..=29.
    for (top, expected) in [(3, (255, 0, 0)), (10, (0, 255, 0)), (17, (0, 0, 255))] {
        for y in top..top + 5 {
            for x in 2..30 {
                assert_eq!(
                    b.pixel(x, y),
                    expected,
                    "bar pixel ({x}, {y}) is not full-intensity {expected:?} — a \
                     dimmed bar can't distinguish a dead channel from a dim one"
                );
            }
        }
    }
}

#[test]
fn renders_the_version_string() {
    let b = test_screen("2.0.0");
    let lit = (VERSION_ROW..VERSION_ROW + GLYPH_ROWS)
        .flat_map(|y| (0..PANEL_WIDTH).map(move |x| (x, y)))
        .filter(|&(x, y)| b.pixel(x, y) != (0, 0, 0))
        .count();
    assert!(lit > 0, "version row is blank");
}

#[test]
fn an_overlong_version_clips_instead_of_overflowing() {
    // Long enough to run well past the right edge several times over.
    let b = test_screen("22.22.22.22.22.22.22.22");

    // Nothing may leak onto the row below the glyphs.
    for x in 0..PANEL_WIDTH {
        assert_eq!(
            b.pixel(x, VERSION_ROW + GLYPH_ROWS),
            (0, 0, 0),
            "glyph row bled onto row {}",
            VERSION_ROW + GLYPH_ROWS
        );
    }
}

#[test]
fn draw_text_skips_glyphs_it_does_not_have() {
    let mut known = Buffer::new();
    draw_text(&mut known, 0, 0, "10", (255, 255, 255));

    let mut interleaved = Buffer::new();
    // Letters other than 'v' are not in the font and must vanish entirely,
    // advancing nothing — otherwise an unexpected version string would
    // shift the digits and misreport the build.
    draw_text(&mut interleaved, 0, 0, "1XYZ0", (255, 255, 255));

    assert_eq!(known.pixels(), interleaved.pixels());
}

#[test]
fn draw_text_returns_the_next_free_column() {
    let mut b = Buffer::new();
    // 3-wide glyph + 1-px gap.
    assert_eq!(draw_text(&mut b, 0, 0, "0", (255, 255, 255)), 4);
    // '.' is 1 wide + 1-px gap.
    assert_eq!(draw_text(&mut b, 0, 0, ".", (255, 255, 255)), 2);
    // Chaining: "v" then "1.0" must land where a single call would.
    let chained = {
        let mut c = Buffer::new();
        let x = draw_text(&mut c, 2, 25, "v", (255, 255, 255));
        draw_text(&mut c, x, 25, "1.0", (255, 255, 255))
    };
    let single = {
        let mut c = Buffer::new();
        draw_text(&mut c, 2, 25, "v1.0", (255, 255, 255))
    };
    assert_eq!(chained, single);
}

#[test]
fn draw_text_clips_off_panel_writes() {
    let mut b = Buffer::new();
    // Starting past the right edge and above the top: every write is out of
    // range, and Canvas::set_pixel must swallow all of them.
    draw_text(
        &mut b,
        PANEL_WIDTH as i32 + 5,
        -10,
        "1234567890",
        (255, 255, 255),
    );
    assert!(b.is_black(), "off-panel text wrote inside the frame");
}
