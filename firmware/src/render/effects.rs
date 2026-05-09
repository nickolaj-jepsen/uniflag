//! Per-flag paint functions. Each is a pure side-effect on `display`,
//! parameterised by `frame` (60 fps tick counter) and, where relevant,
//! `flag_age` (frames since the active flag last changed — used for onset
//! transitions like the red-flag white flash).

use super::anim;
use crate::display::{Display, HEIGHT, WIDTH};
use proto::{Flag, Session, State, WaveLevel};

type Rgb = (u8, u8, u8);

const BLACK: Rgb = (0, 0, 0);
const PIT_BLUE: Rgb = (0, 80, 255);
const PIT_STRIPE_WIDTH: i32 = 2;

const YELLOW: Rgb = (255, 220, 0);
const BLUE: Rgb = (0, 64, 255);
const RED: Rgb = (255, 0, 0);
const GREEN: Rgb = (0, 220, 0);
const WHITE: Rgb = (255, 255, 255);
const ORANGE: Rgb = (255, 90, 0);

pub fn paint(display: &mut Display, state: &State, frame: u32, flag_age: u32, connected: bool) {
    if !connected {
        // Sim/SimHub gone silent past the timeout: blank panel. No pit
        // overlay either — a dark panel must look truly off.
        display.fill(BLACK.0, BLACK.1, BLACK.2);
        return;
    }
    match state.flag {
        Flag::Yellow => paint_yellow(display, state.wave, frame),
        Flag::Red => paint_red(display, state.wave, frame, flag_age),
        Flag::Blue => paint_blue(display, state.wave, frame),
        Flag::Green => paint_green(display, state.wave, frame, flag_age),
        Flag::White => paint_white(display, state.wave, frame),
        Flag::Black => paint_black_flag(display, frame),
        Flag::Orange => paint_orange(display, state.wave, frame),
        Flag::Checkered => paint_checkered(display, frame),
        Flag::None => match state.session {
            // Race in progress (or paused mid-session) → minimal "alive"
            // marker. Anything else → the more visible "armed" indicator.
            Session::Racing | Session::Paused => paint_race_idle(display, frame),
            _ => paint_ready(display, frame),
        },
    }
    if state.in_pit {
        paint_pit_stripe(display);
    }
}

fn paint_yellow(display: &mut Display, wave: WaveLevel, frame: u32) {
    let strobe_hz = match wave {
        WaveLevel::None => 0,
        WaveLevel::Single => 2,
        WaveLevel::Double => 4,
    };
    if strobe_hz != 0 {
        // Waved: clean on/off strobe, no cloth-wave overlay (the strobe is
        // the motion).
        let (r, g, b) = if anim::strobe_60(frame, strobe_hz) {
            YELLOW
        } else {
            BLACK
        };
        display.fill(r, g, b);
        return;
    }
    // Static: pronounced cloth-wave overlay (≈59 %..100 % brightness).
    paint_with_wave(display, YELLOW, frame, 150, 255);
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

fn paint_ready(display: &mut Display, frame: u32) {
    // Pre-race / menus / replay: armed-and-waiting indicator. Hollow green
    // ring centred on the panel, breathing at 0.5 Hz. Reads as "ready for
    // green" — same colour family as the green-flag rendering.
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

fn paint_pit_stripe(display: &mut Display) {
    let (r, g, b) = PIT_BLUE;
    for y in 0..HEIGHT as i32 {
        for dx in 0..PIT_STRIPE_WIDTH {
            display.set_pixel(WIDTH as i32 - 1 - dx, y, r, g, b);
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
