//! Polled brightness-button driver.
//!
//! Wires the Cosmic Unicorn's brightness up / brightness down / sleep
//! buttons (GPIO 21 / 26 / 27, all active-low with internal pull-ups) into
//! a [`BrightnessAction`] event stream consumed by [`crate::render`]. We
//! poll at `POLL_MS` and require `DEBOUNCE_TICKS` consecutive low samples
//! before firing — fast enough that the user doesn't notice latency, slow
//! enough to ignore mechanical bounce.
//!
//! Flash-resident persistence is deliberately not implemented for v1: the
//! `embassy-rp::flash` erase critical-section blocks USB CDC for ~25 ms
//! per 4 KB sector and we'd need long-press detection to avoid one write
//! per click. Easy to add later — see the plan file's phase 11 notes.

use embassy_rp::gpio::{Input, Pull};
use embassy_rp::peripherals::{PIN_21, PIN_26, PIN_27};
use embassy_rp::Peri;
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::channel::Channel;
use embassy_time::{Duration, Timer};

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum BrightnessAction {
    Up,
    Down,
    /// "Sleep" button — soft toggle to a much dimmer state. Doesn't
    /// actually power down the panel.
    SleepToggle,
}

pub type BrightnessChannel = Channel<CriticalSectionRawMutex, BrightnessAction, 4>;

const POLL_MS: u64 = 20;
const DEBOUNCE_TICKS: u8 = 2;

#[embassy_executor::task]
pub async fn run(
    up_pin: Peri<'static, PIN_21>,
    down_pin: Peri<'static, PIN_26>,
    sleep_pin: Peri<'static, PIN_27>,
    chan: &'static BrightnessChannel,
) -> ! {
    let up = Input::new(up_pin, Pull::Up);
    let down = Input::new(down_pin, Pull::Up);
    let sleep = Input::new(sleep_pin, Pull::Up);

    let mut up_d = Debouncer::new();
    let mut down_d = Debouncer::new();
    let mut sleep_d = Debouncer::new();

    loop {
        Timer::after(Duration::from_millis(POLL_MS)).await;

        if up_d.update(up.is_low()) {
            chan.send(BrightnessAction::Up).await;
        }
        if down_d.update(down.is_low()) {
            chan.send(BrightnessAction::Down).await;
        }
        if sleep_d.update(sleep.is_low()) {
            chan.send(BrightnessAction::SleepToggle).await;
        }
    }
}

struct Debouncer {
    streak: u8,
    pressed: bool,
}

impl Debouncer {
    const fn new() -> Self {
        Self {
            streak: 0,
            pressed: false,
        }
    }

    /// Returns `true` once on each rising edge of "stable pressed".
    fn update(&mut self, is_active: bool) -> bool {
        if is_active {
            self.streak = self.streak.saturating_add(1);
        } else {
            self.streak = 0;
        }

        let now_pressed = self.streak >= DEBOUNCE_TICKS;
        let edge = now_pressed && !self.pressed;
        self.pressed = now_pressed;
        edge
    }
}
