//! Per-flag paint functions. Each is a pure side-effect on `display`,
//! parameterised by `frame` (60 fps tick counter) and, where relevant,
//! `flag_age` (frames since the active flag last changed — used for onset
//! transitions like the red-flag white flash).

use super::anim;
use crate::display::{Display, HEIGHT, WIDTH};
use proto::{Caution, Flag, SectorMask, Session, State, WaveLevel};

type Rgb = (u8, u8, u8);

const BLACK: Rgb = (0, 0, 0);

const YELLOW: Rgb = (255, 220, 0);
const BLUE: Rgb = (0, 64, 255);
const RED: Rgb = (255, 0, 0);
const GREEN: Rgb = (0, 220, 0);
const WHITE: Rgb = (255, 255, 255);
const ORANGE: Rgb = (255, 90, 0);

const SECTOR_DIM: Rgb = (40, 30, 0);

const SECTOR_BAND_HEIGHT: i32 = 2;
/// Three 10-px sector segments with 1-px black gaps at cols 10 and 21.
/// Inclusive ranges: S1 cols 0..=9, S2 11..=20, S3 22..=31.
const SECTOR_SEGMENTS: [(i32, i32); 3] = [(0, 9), (11, 20), (22, 31)];

/// Caution-board glyphs: 7 columns × 11 rows. Each row encodes one
/// glyph row in the low 7 bits, MSB = leftmost column (bit 6 = col 0).
const GLYPH_W: i32 = 7;
const GLYPH_H: i32 = 11;
const CAUTION_BORDER: i32 = 2;

#[rustfmt::skip]
const GLYPH_S: [u8; 11] = [
    0b0111110, // .#####.
    0b1100011, // ##...##
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b0111110, // .#####.
    0b0000011, // .....##
    0b0000011, // .....##
    0b0000011, // .....##
    0b1100011, // ##...##
    0b1100011, // ##...##
    0b0111110, // .#####.
];

#[rustfmt::skip]
const GLYPH_C: [u8; 11] = [
    0b0111110, // .#####.
    0b1100011, // ##...##
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100000, // ##.....
    0b1100011, // ##...##
    0b0111110, // .#####.
];

#[rustfmt::skip]
const GLYPH_V: [u8; 11] = [
    0b1100011, // ##...##
    0b1100011, // ##...##
    0b1100011, // ##...##
    0b1100011, // ##...##
    0b0110110, // .##.##.
    0b0110110, // .##.##.
    0b0110110, // .##.##.
    0b0011100, // ..###..
    0b0011100, // ..###..
    0b0011100, // ..###..
    0b0001000, // ...#...
];

pub fn paint(display: &mut Display, state: &State, frame: u32, flag_age: u32, connected: bool) {
    if !connected {
        // Sim/SimHub gone silent past the timeout: blank panel.
        display.fill(BLACK.0, BLACK.1, BLACK.2);
        return;
    }
    // Precedence: Red > Caution (VSC/SC) > other flags. Then sector
    // overlay (suppressed under red).
    match (state.flag, state.caution) {
        (Flag::Red, _) => paint_red(display, state.wave, frame, flag_age),
        (_, Caution::VirtualSafetyCar) => paint_vsc(display, frame),
        (_, Caution::SafetyCar) => paint_safety_car(display, frame),
        (Flag::Yellow, _) => paint_yellow(display, state.wave, frame),
        (Flag::Blue, _) => paint_blue(display, state.wave, frame),
        (Flag::Green, _) => paint_green(display, state.wave, frame, flag_age),
        (Flag::White, _) => paint_white(display, state.wave, frame),
        (Flag::Black, _) => paint_black_flag(display, frame),
        (Flag::Orange, _) => paint_orange(display, state.wave, frame),
        (Flag::Checkered, _) => paint_checkered(display, frame),
        (Flag::None, _) => match state.session {
            // Race in progress (or paused mid-session) → minimal "alive"
            // marker. Anything else → the more visible "armed" indicator,
            // which times out to the same alive marker after 5 s.
            Session::Racing | Session::Paused => paint_race_idle(display, frame),
            _ => paint_ready(display, frame, flag_age),
        },
    }
    // Red flag suppresses the sector band — drivers must stop, extra
    // signalling is noise. Caution states keep it (host might emit
    // VSC + sector-2 yellow simultaneously).
    if !state.sectors.is_empty() && state.flag != Flag::Red {
        paint_sector_band(display, state.sectors, state.wave, frame);
    }
}

fn paint_yellow(display: &mut Display, wave: WaveLevel, frame: u32) {
    match wave {
        WaveLevel::None => {
            // Static: pronounced cloth-wave overlay (≈59 %..100 % brightness).
            paint_with_wave(display, YELLOW, frame, 150, 255);
        }
        WaveLevel::Single => {
            // Whole-panel strobe at 2 Hz.
            let (r, g, b) = if anim::strobe_60(frame, 2) {
                YELLOW
            } else {
                BLACK
            };
            display.fill(r, g, b);
        }
        WaveLevel::Double => {
            // Mimics the real digiflag rendering of double-waved yellow:
            // the panel is split along the anti-diagonal into two triangles
            // that flash out of phase. The eye gets motion across the
            // diagonal axis instead of a uniform flash, which reads as more
            // urgent than the single-waved strobe.
            //
            // 4 Hz alternation, 50 % duty per triangle: at any moment exactly
            // one half is lit. The dividing line itself blinks with the
            // upper-left half so the diagonal reads as a clean edge.
            const PERIOD: u32 = 15; // 4 Hz at 60 fps
            let upper_on = (frame % PERIOD) < PERIOD / 2;
            let anti = WIDTH as i32 - 1;
            for y in 0..HEIGHT as i32 {
                for x in 0..WIDTH as i32 {
                    let upper = (x + y) <= anti;
                    let on = if upper { upper_on } else { !upper_on };
                    let (r, g, b) = if on { YELLOW } else { BLACK };
                    display.set_pixel(x, y, r, g, b);
                }
            }
        }
    }
}

fn paint_red(display: &mut Display, wave: WaveLevel, frame: u32, flag_age: u32) {
    // Sharp white onset flash for ~66 ms (4 frames @ 60 fps), regardless of
    // wave level. Real red-flag panels read very urgently — the flash makes
    // it impossible to miss the moment red drops.
    const ONSET_FRAMES: u32 = 4;
    if flag_age < ONSET_FRAMES {
        let progress = (flag_age * 255 / ONSET_FRAMES) as u8;
        let gb = 255 - progress;
        display.fill(255, gb, gb);
        return;
    }
    let strobe_hz = match wave {
        WaveLevel::None => 0,
        WaveLevel::Single => 2,
        WaveLevel::Double => 4,
    };
    if strobe_hz != 0 {
        // Waved: clean on/off strobe, no cloth-wave overlay.
        let (r, g, b) = if anim::strobe_60(frame, strobe_hz) {
            RED
        } else {
            BLACK
        };
        display.fill(r, g, b);
        return;
    }
    // Static: pronounced cloth-wave overlay, matching yellow's archetype.
    paint_with_wave(display, RED, frame, 150, 255);
}

fn paint_blue(display: &mut Display, wave: WaveLevel, frame: u32) {
    // Cloth-wave overlay; under wave levels, an additional brighter band
    // drifts L→R to mimic a marshal waving the flag back and forth.
    let sweep_step_frames = match wave {
        WaveLevel::None => 0u32,   // no sweep
        WaveLevel::Single => 2u32, // 1 px / 2 frames
        WaveLevel::Double => 1u32, // twice as fast
    };

    if sweep_step_frames == 0 {
        paint_with_wave(display, BLUE, frame, 150, 255);
        return;
    }

    let sweep_pos = ((frame / sweep_step_frames) % WIDTH as u32) as i32;
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let m = if (x - sweep_pos).rem_euclid(WIDTH as i32) < 4 {
                255
            } else {
                anim::wave_mult(x, y, frame, 150, 255)
            };
            let (r, g, b) = anim::scale_rgb(BLUE, m);
            display.set_pixel(x, y, r, g, b);
        }
    }
}

fn paint_green(display: &mut Display, wave: WaveLevel, frame: u32, flag_age: u32) {
    // On flag transition, sweep a bright green band L→R once over ~500 ms.
    // After it clears the panel, settle to solid green (with a faint wave).
    const SWEEP_FRAMES: u32 = 30;
    const BAND_HALF: i32 = 4;
    if flag_age < SWEEP_FRAMES {
        let span = WIDTH as i32 + BAND_HALF * 2;
        let pos = (flag_age as i32 * span / SWEEP_FRAMES as i32) - BAND_HALF;
        let bright: Rgb = (200, 255, 200);
        for y in 0..HEIGHT as i32 {
            for x in 0..WIDTH as i32 {
                let in_band = (x - pos).abs() < BAND_HALF;
                let (r, g, b) = if in_band { bright } else { GREEN };
                display.set_pixel(x, y, r, g, b);
            }
        }
        return;
    }

    // Strobe under wave levels (rarely seen — green is normally static).
    let strobe_hz = match wave {
        WaveLevel::None => 0,
        WaveLevel::Single => 2,
        WaveLevel::Double => 4,
    };
    if strobe_hz != 0 && !anim::strobe_60(frame, strobe_hz) {
        display.fill(BLACK.0, BLACK.1, BLACK.2);
        return;
    }
    paint_with_wave(display, GREEN, frame, 220, 255);
}

fn paint_white(display: &mut Display, wave: WaveLevel, frame: u32) {
    let strobe_hz = match wave {
        WaveLevel::None => 0,
        WaveLevel::Single => 3,
        WaveLevel::Double => 5,
    };
    if strobe_hz != 0 && !anim::strobe_60(frame, strobe_hz) {
        display.fill(BLACK.0, BLACK.1, BLACK.2);
        return;
    }
    paint_with_wave(display, WHITE, frame, 220, 255);
}

fn paint_orange(display: &mut Display, wave: WaveLevel, frame: u32) {
    // Rotating quartered black/orange — closest LED-panel analogue to the
    // real "mechanical" black-and-orange roundel. Two adjacent quadrants
    // are lit at a time and rotate clockwise on a tick.
    let period = match wave {
        WaveLevel::None => 90u32,   // 1.5 s
        WaveLevel::Single => 24u32, // 400 ms
        WaveLevel::Double => 12u32, // 200 ms
    };
    let step = ((frame / period) % 4) as i32;
    let half_w = WIDTH as i32 / 2;
    let half_h = HEIGHT as i32 / 2;
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            // Quadrants numbered clockwise from TL: 0=TL, 1=TR, 2=BR, 3=BL.
            let q = match (y < half_h, x < half_w) {
                (true, true) => 0,
                (true, false) => 1,
                (false, false) => 2,
                (false, true) => 3,
            };
            let on = q == step || q == (step + 1) % 4;
            let (r, g, b) = if on { ORANGE } else { BLACK };
            display.set_pixel(x, y, r, g, b);
        }
    }
}

fn paint_checkered(display: &mut Display, frame: u32) {
    // 4×4 tile pattern scrolling diagonally at 1 px / 8 frames (≈7.5 px/s,
    // one whole tile every ≈530 ms). Slow enough to read as a calm drift
    // rather than a fast scroll. The integer-divide on `(x + offset) / TILE`
    // shifts tile boundaries one pixel per offset increment so the scroll
    // stays smooth.
    const TILE: i32 = 4;
    let off = (frame / 8) as i32;
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let cell = ((x + off).div_euclid(TILE) + (y + off).div_euclid(TILE)) & 1;
            let (r, g, b) = if cell == 0 { BLACK } else { WHITE };
            display.set_pixel(x, y, r, g, b);
        }
    }
}

fn paint_race_idle(display: &mut Display, frame: u32) {
    // Race in progress, no flag waving. Three corners hold a static dim
    // dot; the bottom-right corner breathes very slowly. The pulse is the
    // "I am alive" signal — peripheral, easy to ignore, but enough that
    // you can tell the panel hasn't frozen.
    display.fill(BLACK.0, BLACK.1, BLACK.2);
    const STATIC_M: u8 = 8;
    const PERIOD: u32 = 240; // 0.25 Hz at 60 fps
    let envelope = anim::breathe(frame, PERIOD);
    let pulse_m = 4 + (envelope as u16 * 10 / 255) as u8;
    let max_x = WIDTH as i32 - 1;
    let max_y = HEIGHT as i32 - 1;
    display.set_pixel(0, 0, STATIC_M, STATIC_M, STATIC_M);
    display.set_pixel(max_x, 0, STATIC_M, STATIC_M, STATIC_M);
    display.set_pixel(0, max_y, STATIC_M, STATIC_M, STATIC_M);
    display.set_pixel(max_x, max_y, pulse_m, pulse_m, pulse_m);
}

fn paint_ready(display: &mut Display, frame: u32, flag_age: u32) {
    // Pre-race / menus / replay: armed-and-waiting indicator. Hollow green
    // ring centred on the panel, breathing at 0.5 Hz. Reads as "ready for
    // green" — same colour family as the green-flag rendering. After 5 s
    // we fall back to the same minimal alive marker as race-idle so the
    // orb doesn't sit there indefinitely on a quiet panel.
    const ORB_DURATION_FRAMES: u32 = 300; // 5 s at 60 fps
    if flag_age >= ORB_DURATION_FRAMES {
        paint_race_idle(display, frame);
        return;
    }
    const PERIOD: u32 = 120; // 0.5 Hz at 60 fps
    let envelope = anim::breathe(frame, PERIOD);
    // Map 0..=255 envelope to brightness 40..=200.
    let m = 40 + (envelope as u16 * 160 / 255) as u8;
    display.fill(BLACK.0, BLACK.1, BLACK.2);
    // Centre is between pixels (15.5, 15.5) on a 32×32 grid. Work in
    // half-pixel units (dx2 = 2x - 31) so the ring stays integer-only and
    // perfectly centred. r² in real pixels: 16..=36 → in half-pixel units²
    // (×4): 64..=144.
    for y in 0..HEIGHT as i32 {
        let dy2 = 2 * y - (HEIGHT as i32 - 1);
        for x in 0..WIDTH as i32 {
            let dx2 = 2 * x - (WIDTH as i32 - 1);
            let d_sq = dx2 * dx2 + dy2 * dy2;
            if (64..=144).contains(&d_sq) {
                display.set_pixel(x, y, 0, m, 0);
            }
        }
    }
}

fn paint_black_flag(display: &mut Display, frame: u32) {
    // The black flag (penalty / report-to-pits) needs to read as a
    // deliberate signal, not a powered-off panel. Solid black with a thick
    // white "X" pulsing across both diagonals — a clear penalty mark that
    // doesn't overlap visually with any other flag.
    display.fill(BLACK.0, BLACK.1, BLACK.2);
    const PERIOD: u32 = 100; // 0.6 Hz at 60 fps
    let envelope = anim::breathe(frame, PERIOD);
    let m = (envelope as u16 * 130 / 255) as u8;
    // Square-panel assumption (32×32) — both the main diagonal x==y and
    // the anti-diagonal x+y==31 pass through the corners. `<= 1` makes
    // each diagonal 3 pixels wide per row (~2 px perpendicular).
    let anti = WIDTH as i32 - 1;
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let on_main = (x - y).abs() <= 1;
            let on_anti = (x + y - anti).abs() <= 1;
            if on_main || on_anti {
                display.set_pixel(x, y, m, m, m);
            }
        }
    }
}

fn paint_vsc(display: &mut Display, frame: u32) {
    // White "VSC" on black, yellow border. 7+1+7+1+7 = 23 wide, centred
    // horizontally (4-5 px padding each side, well inside the border).
    paint_caution_board(display, frame, &[&GLYPH_V, &GLYPH_S, &GLYPH_C], 1);
}

fn paint_safety_car(display: &mut Display, frame: u32) {
    // White "SC" on black, yellow border. Same digiflag style as VSC,
    // wider gap between letters since only 2 chars need to fit.
    paint_caution_board(display, frame, &[&GLYPH_S, &GLYPH_C], 4);
}

/// Real motorsport "digiflag" board: white letters on black, yellow
/// border. The border breathes very slowly (~0.25 Hz, 80–100 % range)
/// so the panel reads as live without distracting.
fn paint_caution_board(display: &mut Display, frame: u32, glyphs: &[&[u8; 11]], gap: i32) {
    const BORDER_PERIOD: u32 = 240; // 0.25 Hz at 60 fps
    let envelope = anim::breathe(frame, BORDER_PERIOD);
    let m = 200u8.saturating_add((envelope as u16 * 55 / 255) as u8);
    let border = anim::scale_rgb(YELLOW, m);

    display.fill(BLACK.0, BLACK.1, BLACK.2);

    // Solid yellow rectangle border, `CAUTION_BORDER` px thick.
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let on_border = x < CAUTION_BORDER
                || x >= WIDTH as i32 - CAUTION_BORDER
                || y < CAUTION_BORDER
                || y >= HEIGHT as i32 - CAUTION_BORDER;
            if on_border {
                display.set_pixel(x, y, border.0, border.1, border.2);
            }
        }
    }

    // Centred letter row.
    let n = glyphs.len() as i32;
    let total_w = n * GLYPH_W + (n - 1) * gap;
    let x_left = (WIDTH as i32 - total_w) / 2;
    let y_top = (HEIGHT as i32 - GLYPH_H) / 2;
    for (i, glyph) in glyphs.iter().enumerate() {
        let ox = x_left + i as i32 * (GLYPH_W + gap);
        draw_glyph(display, glyph, ox, y_top, WHITE);
    }
}

/// Stamp a 7×11 1-bpp glyph at `(ox, oy)` in `color`. Bit 6 of each row
/// is the leftmost column, bit 0 is column 6.
fn draw_glyph(display: &mut Display, glyph: &[u8; 11], ox: i32, oy: i32, color: Rgb) {
    for (row, &bits) in glyph.iter().enumerate() {
        for col in 0..GLYPH_W {
            if (bits >> (6 - col)) & 1 != 0 {
                display.set_pixel(ox + col, oy + row as i32, color.0, color.1, color.2);
            }
        }
    }
}

fn paint_sector_band(display: &mut Display, mask: SectorMask, wave: WaveLevel, frame: u32) {
    // Bottom-edge overlay: three 10-px segments with 1-px black gaps at
    // cols 10 and 21 (gap pixels keep whatever the underlying flag drew).
    // Active sectors pulse 2 Hz (4 Hz on B=2). Inactive sectors stay dim
    // so the band is always visible when *any* sector is set — gives a
    // clear "S1 ☐  S2 ▣  S3 ☐" read at a glance.
    let strobe_hz = if matches!(wave, WaveLevel::Double) {
        4
    } else {
        2
    };
    let on = anim::strobe_60(frame, strobe_hz);
    let y_start = HEIGHT as i32 - SECTOR_BAND_HEIGHT;
    for (idx, &(x_lo, x_hi)) in SECTOR_SEGMENTS.iter().enumerate() {
        let sector = (idx + 1) as u8;
        let active = mask.contains(sector);
        for y in y_start..HEIGHT as i32 {
            for x in x_lo..=x_hi {
                let (r, g, b) = if active {
                    if on {
                        let m = anim::wave_mult(x, y, frame, 180, 255);
                        anim::scale_rgb(YELLOW, m)
                    } else {
                        BLACK
                    }
                } else {
                    SECTOR_DIM
                };
                display.set_pixel(x, y, r, g, b);
            }
        }
    }
}

fn paint_with_wave(display: &mut Display, base: Rgb, frame: u32, lo_mult: u8, hi_mult: u8) {
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let m = anim::wave_mult(x, y, frame, lo_mult, hi_mult);
            let (r, g, b) = anim::scale_rgb(base, m);
            display.set_pixel(x, y, r, g, b);
        }
    }
}
