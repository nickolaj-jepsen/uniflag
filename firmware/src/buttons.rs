//! Polled button driver.
//!
//! Wires the Cosmic Unicorn's three buttons (GPIO 21 / 26 / 27, all
//! active-low with internal pull-ups) into `proto` `ButtonEvent`s on the
//! CDC TX channel. The host owns button policy, so all this task does is
//! debounce, classify, and report (only while a handshaken host is live —
//! `BUTTON_REPORTING` in `main.rs`). The one local side effect: a long
//! press (any button) also toggles the runtime's test screen.
//!
//! Classification happens **on release** — a press held for at least
//! [`LONG_PRESS_TICKS`] polls reports `PressKind::Long`, anything shorter
//! `PressKind::Short`, and a long press never *also* fires a short one
//! (docs/protocol.md §ButtonEvent). We poll at `POLL_MS` and require
//! `DEBOUNCE_TICKS` consecutive agreeing samples before accepting either
//! edge — fast enough that the user doesn't notice latency, slow enough
//! to ignore mechanical bounce.

use core::sync::atomic::Ordering;

use embassy_rp::gpio::{Input, Pull};
use embassy_rp::peripherals::{PIN_21, PIN_26, PIN_27};
use embassy_rp::Peri;
use embassy_time::{Duration, Timer};

use proto::packet::{Button, PressKind};

use crate::{TestToggleChannel, TxChannel, TxEvent, BUTTON_REPORTING};

const POLL_MS: u64 = 20;
const DEBOUNCE_TICKS: u8 = 2;

/// Hold for at least this many polls (600 ms) to classify as a long
/// press. Not a wire contract — the protocol only distinguishes the two
/// kinds — so this is tuned for feel: comfortably past an emphatic tap,
/// short enough not to feel like a stuck button.
const LONG_PRESS_TICKS: u16 = 600 / POLL_MS as u16;

#[embassy_executor::task]
pub async fn run(
    up_pin: Peri<'static, PIN_21>,
    down_pin: Peri<'static, PIN_26>,
    sleep_pin: Peri<'static, PIN_27>,
    tx: &'static TxChannel,
    test_toggle: &'static TestToggleChannel,
) -> ! {
    let mut keys = [
        (
            Input::new(up_pin, Pull::Up),
            Debouncer::new(),
            Button::BrightnessUp,
        ),
        (
            Input::new(down_pin, Pull::Up),
            Debouncer::new(),
            Button::BrightnessDown,
        ),
        (
            Input::new(sleep_pin, Pull::Up),
            Debouncer::new(),
            Button::Sleep,
        ),
    ];

    loop {
        Timer::after(Duration::from_millis(POLL_MS)).await;

        for (input, debouncer, button) in &mut keys {
            let Some(held_ticks) = debouncer.update(input.is_low()) else {
                continue;
            };
            let kind = if held_ticks >= LONG_PRESS_TICKS {
                PressKind::Long
            } else {
                PressKind::Short
            };
            // Report only while a handshaken host is live (see
            // BUTTON_REPORTING in main.rs) — a press queued with nobody
            // reading would flush ahead of the next session's HelloAck.
            // Stale presses are worthless to a future host, so drop, not
            // defer. try_send for the same reason: if the queue fills,
            // drop the event rather than stall the poll loop (and with
            // it the test-screen toggle).
            if BUTTON_REPORTING.load(Ordering::Relaxed) {
                let _ = tx.try_send(TxEvent::Button {
                    button: button.to_byte(),
                    kind: kind.to_byte(),
                });
            }
            if kind == PressKind::Long {
                let _ = test_toggle.try_send(());
            }
        }
    }
}

/// Symmetric debouncer that tracks hold duration. Both edges need
/// `DEBOUNCE_TICKS` consecutive agreeing samples, so the reported hold
/// time is accurate to ± `DEBOUNCE_TICKS` polls (± 40 ms) — noise against
/// the 600 ms long-press threshold.
struct Debouncer {
    /// Consecutive samples disagreeing with the debounced state.
    streak: u8,
    pressed: bool,
    /// Polls since the debounced press edge. Saturating — a 21-minute
    /// hold is still just "long".
    held_ticks: u16,
}

impl Debouncer {
    const fn new() -> Self {
        Self {
            streak: 0,
            pressed: false,
            held_ticks: 0,
        }
    }

    /// Feed one poll sample. Returns `Some(held_ticks)` exactly once per
    /// press, on the debounced release edge — classification on release
    /// is what guarantees a long press never also fires a short.
    fn update(&mut self, is_active: bool) -> Option<u16> {
        if is_active == self.pressed {
            self.streak = 0;
        } else {
            self.streak = self.streak.saturating_add(1);
        }
        if self.pressed {
            self.held_ticks = self.held_ticks.saturating_add(1);
        }

        if self.streak >= DEBOUNCE_TICKS {
            self.streak = 0;
            self.pressed = !self.pressed;
            if self.pressed {
                self.held_ticks = 0;
            } else {
                return Some(self.held_ticks);
            }
        }
        None
    }
}
