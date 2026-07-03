//! Monotonic-deadline pacing arithmetic (pure).
//!
//! Frame `n`'s deadline is `epoch + n / fps` seconds on a monotonic
//! clock. Each deadline is computed as an absolute product from the frame
//! index — never accumulated increment by increment — so rounding cannot
//! drift the schedule. When the sender falls behind (a device stall, an
//! OS scheduling hiccup), missed deadlines are **skipped, never slewed,
//! never rebased**: the device's stream-as-heartbeat contract
//! (docs/protocol.md) assumes the cadence stays honest, and a catch-up
//! burst or a shifted epoch would lie about liveness.

/// Nanoseconds per second, as the u128 the arithmetic below runs in.
pub const NANOS_PER_SEC: u128 = 1_000_000_000;

/// Absolute deadline of frame `index`, in nanoseconds since the epoch.
pub fn deadline_nanos(index: u64, fps: u32) -> u128 {
    u128::from(index) * NANOS_PER_SEC / u128::from(fps.max(1))
}

/// The next frame index to schedule after completing frame `prev`, given
/// the monotonic time elapsed since the epoch: at least `prev + 1`, but
/// skipping any deadlines that have already passed. The returned index's
/// deadline is never in the past (modulo sub-nanosecond rounding), so the
/// caller either sleeps until it or sends immediately — it never bursts
/// to catch up.
pub fn next_frame_index(prev: u64, elapsed_nanos: u128, fps: u32) -> u64 {
    // First index whose deadline (index / fps seconds) is >= elapsed.
    let due = (elapsed_nanos * u128::from(fps.max(1))).div_ceil(NANOS_PER_SEC);
    let due = u64::try_from(due).unwrap_or(u64::MAX);
    due.max(prev + 1)
}

#[cfg(test)]
mod tests {
    use super::*;

    const FPS: u32 = 30;
    const MS: u128 = 1_000_000;

    #[test]
    fn on_schedule_advances_one_frame_at_a_time() {
        // Finished frame 0 quickly: the next deadline is frame 1's.
        assert_eq!(next_frame_index(0, 0, FPS), 1);
        assert_eq!(next_frame_index(0, 10 * MS, FPS), 1);
        // Exactly at frame 1's deadline (33.33.. ms) it is not missed yet.
        assert_eq!(next_frame_index(0, deadline_nanos(1, FPS), FPS), 1);
        assert_eq!(next_frame_index(5, deadline_nanos(6, FPS), FPS), 6);
    }

    #[test]
    fn a_100ms_stall_at_30fps_skips_to_index_3() {
        // From the epoch, a simulated 100 ms stall after frame 0: frames 1
        // (33.3 ms) and 2 (66.7 ms) are missed and skipped; frame 3's
        // deadline is exactly 100 ms — the index jumps by 3, no more.
        assert_eq!(next_frame_index(0, 100 * MS, FPS), 3);
        // A hair past frame 3's deadline skips it too (skip, not slew).
        assert_eq!(next_frame_index(0, 100 * MS + 1, FPS), 4);
    }

    #[test]
    fn skipping_keeps_deadlines_on_the_absolute_grid() {
        // After any stall, the chosen index's deadline still sits on the
        // epoch-anchored grid — no rebase: at 30 fps every third frame
        // lands on an exact 100 ms multiple, stalls or not.
        let next = next_frame_index(0, 100 * MS, FPS);
        assert_eq!(deadline_nanos(next, FPS), 100 * MS);
        let next = next_frame_index(7, 1_234 * MS, FPS);
        assert_eq!(next, 38); // ceil(1.234 s * 30) = 38
        assert_eq!(deadline_nanos(38, FPS), 1_266_666_666); // 38/30 s, floor
    }

    #[test]
    fn deadline_grid_does_not_drift_long_run() {
        // Absolute-product deadlines: 21600 frames at 30 fps is exactly
        // 12 minutes, not 12 minutes minus accumulated truncation.
        assert_eq!(deadline_nanos(21_600, FPS), 720 * NANOS_PER_SEC);
        assert_eq!(deadline_nanos(3, FPS), 100 * MS);
        assert_eq!(deadline_nanos(30, FPS), NANOS_PER_SEC);
    }

    #[test]
    fn next_index_is_always_strictly_ahead() {
        // Even when the clock says an earlier index is due, the index
        // never repeats or goes backwards.
        assert_eq!(next_frame_index(10, 0, FPS), 11);
        assert_eq!(next_frame_index(10, 50 * MS, FPS), 11);
    }

    #[test]
    fn zero_fps_is_clamped_rather_than_dividing_by_zero() {
        assert_eq!(deadline_nanos(3, 0), 3 * NANOS_PER_SEC);
        assert_eq!(next_frame_index(0, NANOS_PER_SEC, 0), 1);
    }
}
