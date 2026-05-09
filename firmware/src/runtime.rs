//! Embassy task that drives the panel.
//!
//! Holds the hardware [`Display`] and the persisted [`FlashStorage`].
//! Runs a 60 fps animation loop: every tick we recompute the whole panel
//! from the current [`State`] plus a frame counter, so flag effects
//! (strobes, sweeps, scrolling chequered, breathing splash) stay live.
//!
//! Per-flag rendering details and animation primitives live in the
//! `uniflag-render` crate (`effects`, `anim`); the brightness/sleep
//! state machine lives there too as
//! [`uniflag_render::BrightnessController`]. This module just wires the
//! signals/timers/persistence to those pieces.
//!
//! v3 rendering rules (per-flag, "as realistic as possible").
//!
//! Per-flag base layer (renders unless overridden by precedence below):
//! - **Yellow**: solid + faint diagonal cloth-wave overlay; under wave
//!   level 1 (single-waved) a 2 Hz strobe; under level 2 (double-waved) a
//!   4 Hz strobe.
//! - **Red**: white onset flash for ~66 ms on transition into red, then
//!   solid + faint wave; strobes at 2 Hz / 4 Hz under wave levels.
//! - **Blue**: 0.5 Hz breathing under static; 2-3 Hz breathing + a brighter
//!   sweep band under wave levels.
//! - **Green**: bright band sweep L→R for ~500 ms on transition, then
//!   solid + faint wave.
//! - **White**: solid + wave; 3 Hz / 5 Hz strobe under wave levels.
//! - **Black**: solid black with a slow-pulsing white "X" across both
//!   diagonals — a clear penalty mark, distinct from a powered-off panel
//!   and from every other flag rendering.
//! - **Orange (mechanical)**: rotating quartered black/orange pattern,
//!   period 1 s / 250 ms / ~133 ms under wave 0/1/2.
//! - **Checkered**: 4×4 tiles scrolling diagonally at 30 px/s.
//! - **None + Racing/Paused**: minimal "alive" marker — three static dim
//!   corner dots and one slow-pulsing dot in the bottom-right.
//! - **None + PreRace/PostRace/Replay/Unknown**: "ready" indicator —
//!   centred green ring breathing at 0.5 Hz for the first 5 s, then
//!   fades to the same minimal alive marker as race-idle.
//!
//! Precedence — what actually fills the panel when multiple states are
//! active (highest wins):
//! 1. **Disconnected** (no host updates for `CONNECT_TIMEOUT`): all LEDs
//!    off, no overlays. Boot state until SimHub starts emitting.
//! 2. **Red flag** wins over caution and any other flag — drivers must
//!    react to red regardless of session-wide state.
//! 3. **Caution (`C=V` or `C=S`)** wins over all flags except red.
//!    Both render as a real-motorsport "digiflag" board: white letters
//!    on black, surrounded by a 2-px yellow border that breathes very
//!    slowly (0.25 Hz, ~80–100 % brightness) so the panel reads as live
//!    without distracting.
//!    - **VSC (`C=V`)**: white `VSC` letters (7×11 glyphs, 1-px gaps).
//!    - **Safety Car (`C=S`)**: white `SC` letters (same glyphs, wider
//!      gap since only two letters need to fit).
//! 4. Otherwise the per-flag base layer above.
//!
//! Overlays drawn on top of the base layer:
//! - **Sector band (`Z=...`)**: bottom 2 rows, three 10-px segments with
//!   1-px black gaps. Active sectors pulse yellow at 2 Hz (4 Hz on
//!   `B=2`); inactive sectors stay dim yellow so the band is always
//!   visible when any sector is set. Suppressed under red flag.

use embassy_futures::select::{select3, Either3};
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::signal::Signal;
use embassy_time::{Duration, Instant, Timer};

use proto::State;
use uniflag_render::BrightnessController;

use crate::buttons::BrightnessChannel;
use crate::display::Display;
use crate::storage::{self, FlashStorage};

/// Animation tick. ~60 fps; full-panel per-pixel paint costs ≈ 330 µs so
/// this is well under 2 % CPU.
const FRAME_TICK_MS: u64 = 16;

/// Default brightness on boot when nothing is persisted yet. The Cosmic
/// Unicorn at 100% is uncomfortably bright in a typical sim-rig setup;
/// this is around 30% perceived after gamma.
const DEFAULT_BRIGHTNESS: u8 = 80;

/// How long we'll keep showing the last received state before declaring
/// the host disconnected and blanking the panel. SimHub pushes at 5 Hz
/// (200 ms), so 1.5 s tolerates ~7 dropped messages.
const CONNECT_TIMEOUT: Duration = Duration::from_millis(1500);

pub async fn run(
    mut display: Display,
    mut flash: FlashStorage,
    state_signal: &'static Signal<CriticalSectionRawMutex, State>,
    brightness_chan: &'static BrightnessChannel,
) -> ! {
    let initial_brightness = storage::load_brightness(&mut flash).unwrap_or(DEFAULT_BRIGHTNESS);
    let mut brightness = BrightnessController::new(initial_brightness);

    let mut state = State::default();
    let mut frame: u32 = 0;
    let mut flag_changed_at: u32 = 0;
    // `None` until the first message arrives — the panel boots dark and
    // stays that way until the host emits, rather than briefly showing a
    // default `State`. Set on every state arrival, read on each tick.
    let mut last_state_at: Option<Instant> = None;
    let frame_tick = Duration::from_millis(FRAME_TICK_MS);

    display.set_brightness(brightness.current());
    uniflag_render::effects::paint(&mut display, &state, frame, 0, false);
    display.present().await;

    loop {
        match select3(
            state_signal.wait(),
            Timer::after(frame_tick),
            brightness_chan.receive(),
        )
        .await
        {
            Either3::First(new) => {
                if new.flag != state.flag {
                    flag_changed_at = frame;
                }
                state = new;
                last_state_at = Some(Instant::now());
            }
            Either3::Second(_) => {
                frame = frame.wrapping_add(1);
            }
            Either3::Third(action) => {
                brightness.apply(action, now_ms());
                display.set_brightness(brightness.current());
            }
        }

        let age = frame.wrapping_sub(flag_changed_at);
        let connected = last_state_at.is_some_and(|t| t.elapsed() < CONNECT_TIMEOUT);
        uniflag_render::effects::paint(&mut display, &state, frame, age, connected);
        display.present().await;

        if let Some(b) = brightness.maybe_save(now_ms()) {
            storage::save_brightness(&mut flash, b);
        }
    }
}

fn now_ms() -> u64 {
    Instant::now().as_millis()
}
