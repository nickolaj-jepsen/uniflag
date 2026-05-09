//! Unit tests for the brightness/sleep state machine.

use uniflag_render::brightness::{Action, BrightnessController};

const STEP: u8 = BrightnessController::STEP;
const SLEEP: u8 = BrightnessController::SLEEP;
const DEBOUNCE: u64 = BrightnessController::SAVE_DEBOUNCE_MS;

#[test]
fn up_increments_by_step() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Up, 0);
    assert_eq!(c.current(), 80 + STEP);
}

#[test]
fn down_decrements_by_step() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Down, 0);
    assert_eq!(c.current(), 80 - STEP);
}

#[test]
fn down_saturates_at_zero() {
    let mut c = BrightnessController::new(STEP - 1);
    c.apply(Action::Down, 0);
    assert_eq!(c.current(), 0);
}

#[test]
fn up_saturates_at_255() {
    let mut c = BrightnessController::new(250);
    c.apply(Action::Up, 0);
    assert_eq!(c.current(), 255);
}

#[test]
fn sleep_toggle_dims_then_restores() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::SleepToggle, 0);
    assert_eq!(c.current(), SLEEP);
    c.apply(Action::SleepToggle, 100);
    assert_eq!(c.current(), 80);
}

#[test]
fn sleep_restores_to_at_least_one_step() {
    // If last_awake somehow became very small, waking should still
    // produce a visible brightness rather than match the sleep level.
    let mut c = BrightnessController::new(2);
    c.apply(Action::SleepToggle, 0);
    // Already at/below sleep — toggle wakes to last_awake.max(STEP).
    assert_eq!(c.current(), STEP);
}

#[test]
fn up_while_sleeping_steps_from_sleep() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::SleepToggle, 0);
    assert_eq!(c.current(), SLEEP);
    c.apply(Action::Up, 100);
    assert_eq!(c.current(), SLEEP + STEP);
}

#[test]
fn save_debounced_to_window() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Up, 0);
    assert_eq!(c.maybe_save(DEBOUNCE - 1), None);
    assert_eq!(c.maybe_save(DEBOUNCE), Some(80 + STEP));
}

#[test]
fn save_returns_none_after_persist_until_dirty_again() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Up, 0);
    assert_eq!(c.maybe_save(DEBOUNCE), Some(80 + STEP));
    // Same window, asked again: nothing to do.
    assert_eq!(c.maybe_save(DEBOUNCE + 1000), None);
    // Press up again: dirty window restarts.
    c.apply(Action::Up, 5_000);
    assert_eq!(c.maybe_save(5_000 + DEBOUNCE - 1), None);
    assert_eq!(c.maybe_save(5_000 + DEBOUNCE), Some(80 + 2 * STEP));
}

#[test]
fn rapid_changes_coalesce_to_one_save() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Up, 0);
    c.apply(Action::Up, 100);
    c.apply(Action::Up, 200);
    assert_eq!(c.maybe_save(200 + DEBOUNCE - 1), None);
    assert_eq!(c.maybe_save(200 + DEBOUNCE), Some(80 + 3 * STEP));
    // Still no further save in the same window.
    assert_eq!(c.maybe_save(200 + DEBOUNCE + 5_000), None);
}

#[test]
fn return_to_saved_value_clears_dirty() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::Up, 0);
    c.apply(Action::Down, 100);
    // Net change: zero. Should not produce a save.
    assert_eq!(c.maybe_save(100 + DEBOUNCE), None);
    assert_eq!(c.current(), 80);
}

#[test]
fn no_save_for_no_op_action() {
    // If brightness is already 255 and user presses Up, current stays
    // 255 — no dirty bit, no save.
    let mut c = BrightnessController::new(255);
    c.apply(Action::Up, 0);
    assert_eq!(c.current(), 255);
    assert_eq!(c.maybe_save(DEBOUNCE), None);
}

#[test]
fn sleep_then_wake_persists_each_distinct_value() {
    let mut c = BrightnessController::new(80);
    c.apply(Action::SleepToggle, 0);
    assert_eq!(c.maybe_save(DEBOUNCE), Some(SLEEP));
    c.apply(Action::SleepToggle, 5_000);
    assert_eq!(c.maybe_save(5_000 + DEBOUNCE), Some(80));
}
