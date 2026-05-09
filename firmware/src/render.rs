//! Flag → display renderer.
//!
//! Holds a [`Display`] and a [`proto::State`]. Every time the state changes
//! (signalled via the global `STATE_SIGNAL`), or every `BLINK_TICK_MS` for
//! animation, redraw the panel.
//!
//! v1 rendering rules:
//! - `Flag::None`: dim splash (1px border, dim white) when racing,
//!   black when not.
//! - solid flag colours (yellow / blue / black / white / red / green /
//!   orange) fill the whole panel.
//! - `Flag::Checkered`: 4×4 chequered tile pattern, black/white.
//! - blink (`B=1`): toggle the active flag colour with black at
//!   `BLINK_TICK_MS` cadence.
//! - in-pit (`P=1`): paint the rightmost column in pit-blue regardless
//!   of flag.

use embassy_futures::select::{select3, Either3};
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::signal::Signal;
use embassy_time::{Duration, Timer};

use crate::buttons::{BrightnessAction, BrightnessChannel};
use crate::display::{Display, HEIGHT, WIDTH};
use proto::{Flag, State};

const BLINK_TICK_MS: u64 = 250;
const PIT_STRIPE_WIDTH: i32 = 2;
/// Default brightness on boot. The Cosmic Unicorn at 100% is uncomfortably
/// bright in a typical sim-rig setup; this is around 30% perceived after
/// gamma.
const DEFAULT_BRIGHTNESS: u8 = 80;
/// Step size per button press. ~5% of full range.
const BRIGHTNESS_STEP: u8 = 12;
/// What the SleepToggle button drops brightness to (and restores on the
/// next press if not adjusted otherwise).
const SLEEP_BRIGHTNESS: u8 = 6;

type Rgb = (u8, u8, u8);

const BLACK: Rgb = (0, 0, 0);
const WHITE: Rgb = (255, 255, 255);
const SPLASH_DIM: Rgb = (12, 12, 12);
const PIT_BLUE: Rgb = (0, 80, 255);

pub async fn run(
    mut display: Display,
    state_signal: &'static Signal<CriticalSectionRawMutex, State>,
    brightness_chan: &'static BrightnessChannel,
) -> ! {
    let mut state = State::default();
    let mut brightness = DEFAULT_BRIGHTNESS;
    let mut last_awake_brightness = DEFAULT_BRIGHTNESS;
    let mut blink_phase = false;
    let blink_tick = Duration::from_millis(BLINK_TICK_MS);

    display.set_brightness(brightness);
    paint(&mut display, &state, false);

    loop {
        match select3(
            state_signal.wait(),
            Timer::after(blink_tick),
            brightness_chan.receive(),
        )
        .await
        {
            Either3::First(new) => {
                state = new;
                blink_phase = false;
                paint(&mut display, &state, blink_phase);
            }
            Either3::Second(_) => {
                if state.blink {
                    blink_phase = !blink_phase;
                    paint(&mut display, &state, blink_phase);
                }
                // if not blinking, the panel content is already correct —
                // just keep waiting.
            }
            Either3::Third(action) => {
                brightness = apply_brightness(action, brightness, &mut last_awake_brightness);
                display.set_brightness(brightness);
                paint(&mut display, &state, blink_phase);
            }
        }
    }
}

fn apply_brightness(action: BrightnessAction, current: u8, last_awake: &mut u8) -> u8 {
    match action {
        BrightnessAction::Up => {
            let next = current.saturating_add(BRIGHTNESS_STEP);
            *last_awake = next;
            next
        }
        BrightnessAction::Down => {
            let next = current.saturating_sub(BRIGHTNESS_STEP);
            *last_awake = next;
            next
        }
        BrightnessAction::SleepToggle => {
            if current <= SLEEP_BRIGHTNESS {
                // Wake.
                (*last_awake).max(BRIGHTNESS_STEP)
            } else {
                // Sleep — remember the current brightness for wake.
                *last_awake = current;
                SLEEP_BRIGHTNESS
            }
        }
    }
}

fn paint(display: &mut Display, state: &State, blink_off: bool) {
    let body = body_for(state);

    if blink_off && state.blink {
        // Blink-off frame: paint black instead of the flag colour.
        display.fill(BLACK.0, BLACK.1, BLACK.2);
    } else {
        match body {
            Body::Solid(c) => display.fill(c.0, c.1, c.2),
            Body::Checkered => paint_checkered(display),
            Body::Splash => paint_splash(display),
        }
    }

    if state.in_pit {
        paint_pit_stripe(display);
    }
}

enum Body {
    Solid(Rgb),
    Checkered,
    Splash,
}

fn body_for(state: &State) -> Body {
    match state.flag {
        Flag::Yellow => Body::Solid((255, 220, 0)),
        Flag::Blue => Body::Solid((0, 64, 255)),
        Flag::Black => Body::Solid(BLACK),
        Flag::White => Body::Solid(WHITE),
        Flag::Red => Body::Solid((255, 0, 0)),
        Flag::Green => Body::Solid((0, 220, 0)),
        Flag::Orange => Body::Solid((255, 90, 0)),
        Flag::Checkered => Body::Checkered,
        // No active flag → dim border splash. This is also the boot state
        // (Session::Unknown), so the user gets a "panel alive, no host yet"
        // indication immediately after init.
        Flag::None => Body::Splash,
    }
}

fn paint_checkered(display: &mut Display) {
    const TILE: i32 = 4;
    for y in 0..HEIGHT as i32 {
        for x in 0..WIDTH as i32 {
            let cell = (x / TILE + y / TILE) & 1;
            let c = if cell == 0 { BLACK } else { WHITE };
            display.set_pixel(x, y, c.0, c.1, c.2);
        }
    }
}

fn paint_splash(display: &mut Display) {
    // Dim border around an otherwise-black panel. Indicates "alive,
    // connected, nothing happening on track".
    display.fill(BLACK.0, BLACK.1, BLACK.2);
    let (r, g, b) = SPLASH_DIM;
    for x in 0..WIDTH as i32 {
        display.set_pixel(x, 0, r, g, b);
        display.set_pixel(x, HEIGHT as i32 - 1, r, g, b);
    }
    for y in 0..HEIGHT as i32 {
        display.set_pixel(0, y, r, g, b);
        display.set_pixel(WIDTH as i32 - 1, y, r, g, b);
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
