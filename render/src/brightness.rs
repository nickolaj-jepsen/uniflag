//! [`BrightnessController`] — pure state machine for the brightness/sleep
//! buttons and the debounced flash-persistence policy.
//!
//! Kept here (rather than in firmware) so it can be unit-tested on host
//! without faking embassy timers or flash. The caller passes a monotonic
//! millisecond timestamp into [`apply`](BrightnessController::apply) and
//! [`maybe_save`](BrightnessController::maybe_save); on the firmware that
//! comes from `embassy_time::Instant::now().as_millis()`.

/// Button-driven action. Comes from the buttons task on the firmware,
/// or from a test on host.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Action {
    /// Step brightness up by [`BrightnessController::STEP`].
    Up,
    /// Step brightness down by [`BrightnessController::STEP`].
    Down,
    /// Soft toggle to/from a much dimmer state. Doesn't actually power
    /// down the panel.
    SleepToggle,
}

/// State machine that owns the displayed brightness, the
/// "remembered awake" level for sleep-toggle, and the dirty/debounce
/// tracking for flash persistence.
#[derive(Copy, Clone, Debug)]
pub struct BrightnessController {
    current: u8,
    /// What to restore to when waking from sleep. Updated on every
    /// awake-state change (Up / Down) so the "wake" level always tracks
    /// the user's last preferred bright value.
    last_awake: u8,
    /// Last value successfully persisted to flash. Used to suppress
    /// no-op writes when the user toggles back to the saved value.
    last_saved: u8,
    /// When the current value first diverged from `last_saved`. `None`
    /// if `current == last_saved`.
    dirty_since_ms: Option<u64>,
}

impl BrightnessController {
    /// Step size per Up/Down action. ~5 % of the 0..=255 range.
    pub const STEP: u8 = 12;
    /// Brightness the SleepToggle drops to when awake.
    pub const SLEEP: u8 = 6;
    /// Quiescence window before a brightness change is committed to
    /// flash. Coalesces rapid clicks into a single ~25 ms erase/write.
    pub const SAVE_DEBOUNCE_MS: u64 = 2000;

    /// New controller seeded with the persisted value from flash (or a
    /// caller-chosen default on a virgin sector).
    pub const fn new(initial: u8) -> Self {
        Self {
            current: initial,
            last_awake: initial,
            last_saved: initial,
            dirty_since_ms: None,
        }
    }

    /// Currently displayed brightness, 0..=255.
    pub const fn current(&self) -> u8 {
        self.current
    }

    /// Apply a button action. `now_ms` is a monotonic millisecond
    /// timestamp; the value only matters relative to itself across
    /// successive calls.
    pub fn apply(&mut self, action: Action, now_ms: u64) {
        let next = match action {
            Action::Up => {
                let n = self.current.saturating_add(Self::STEP);
                self.last_awake = n;
                n
            }
            Action::Down => {
                let n = self.current.saturating_sub(Self::STEP);
                self.last_awake = n;
                n
            }
            Action::SleepToggle => {
                if self.current <= Self::SLEEP {
                    // Already sleeping → wake to the remembered awake
                    // level (clamped to at least one step so we don't
                    // appear to do nothing when last_awake was very low).
                    self.last_awake.max(Self::STEP)
                } else {
                    // Awake → remember this value and dim.
                    self.last_awake = self.current;
                    Self::SLEEP
                }
            }
        };

        if next != self.current {
            self.current = next;
            // Restart the debounce window — the user is still actively
            // adjusting; we only commit once they settle.
            if next != self.last_saved {
                self.dirty_since_ms = Some(now_ms);
            } else {
                // They came back to the saved value; nothing to write.
                self.dirty_since_ms = None;
            }
        }
    }

    /// Returns `Some(brightness)` once the debounce window has elapsed
    /// and the current value differs from the last persisted one. The
    /// caller is expected to write that value to flash, after which
    /// further calls return `None` until the user changes brightness
    /// again. Internally records `last_saved` on the `Some` branch.
    pub fn maybe_save(&mut self, now_ms: u64) -> Option<u8> {
        let dirty_since = self.dirty_since_ms?;
        if now_ms.saturating_sub(dirty_since) < Self::SAVE_DEBOUNCE_MS {
            return None;
        }
        if self.current == self.last_saved {
            // Defensive: shouldn't happen given `apply`'s bookkeeping,
            // but treat it as clean.
            self.dirty_since_ms = None;
            return None;
        }
        self.last_saved = self.current;
        self.dirty_since_ms = None;
        Some(self.current)
    }
}
