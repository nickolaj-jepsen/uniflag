//! Embassy task that drives the panel.
//!
//! The firmware renders nothing of its own during normal operation — the
//! host streams complete RGB888 frames at 30 fps (docs/protocol.md,
//! "stream-as-heartbeat") and this task blits them. The signals and their
//! precedence rules live host-side; their spec is `docs/flag-grammar.md`.
//!
//! Select-loop arms worth knowing about:
//!
//! - **Frame** → paint into the back buffer, *then* `present().await`, so a
//!   mid-paint refresh never tears.
//! - **Brightness** → applied when the next streamed frame blits, and
//!   deliberately *not* to the local screens: those always paint full
//!   brightness so a stale dim/sleep value from a host that then died can't
//!   render them invisible (§7 exists to distinguish "device alive, no host"
//!   from a powered-off panel).
//! - **Long press** → toggle the local test screen. Streamed frames are
//!   still consumed while it shows, so un-toggling snaps straight back.
//! - **Tick** (16 ms = the workspace "60 fps") → advance the local frame
//!   counter and repaint whichever local screen is active. With no decodable
//!   Frame for [`CONNECT_TIMEOUT`] that is the §7a fallback, which is also
//!   the boot state.

use core::sync::atomic::Ordering;

use embassy_futures::select::{select4, Either4};
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::signal::Signal;
use embassy_sync::zerocopy_channel::Receiver as FrameReceiver;
use embassy_time::{Duration, Instant, Ticker};

use crate::display::Display;
use crate::{FrameBuf, TestToggleChannel, BUTTON_REPORTING, FW_VERSION};

/// Local-screen animation tick. 16 ms — the "60 fps" convention every
/// frame count in docs/flag-grammar.md assumes.
const FRAME_TICK: Duration = Duration::from_millis(16);

/// How long without a decodable Frame before we drop to the local
/// fallback screen. The host streams at 30 fps, so 1.5 s tolerates ~45
/// missed frames (docs/protocol.md §Stream-as-heartbeat).
const CONNECT_TIMEOUT: Duration = Duration::from_millis(1500);

pub async fn run(
    mut display: Display,
    mut frames: FrameReceiver<'static, CriticalSectionRawMutex, FrameBuf>,
    brightness: &'static Signal<CriticalSectionRawMutex, u8>,
    test_toggle: &'static TestToggleChannel,
) -> ! {
    // Ticker-driven only, so the fallback ember keeps its 8 s glide period
    // no matter what else is going on.
    let mut frame: u32 = 0;
    // `None` until the first frame arrives, so boot shows the fallback.
    let mut last_frame_at: Option<Instant> = None;
    // 255 is the unity multiplier, matching display.rs's boot default.
    let mut host_brightness: u8 = 255;
    let mut test_mode = false;
    let mut ticker = Ticker::every(FRAME_TICK);

    // Frame 0 lights the ember core at full envelope, so power-up shows
    // life immediately.
    screens::paint_fallback(&mut display, frame);
    display.present().await;

    loop {
        // Set by the arms after which a local screen may need repainting;
        // the streamed path presents inline in its own arm.
        let mut paint_local = false;

        match select4(
            frames.receive(),
            brightness.wait(),
            test_toggle.receive(),
            ticker.next(),
        )
        .await
        {
            Either4::First(pixels) => {
                last_frame_at = Some(Instant::now());
                let show = !test_mode;
                if show {
                    display.set_brightness(host_brightness);
                    display.blit_rgb888(pixels);
                }
                frames.receive_done();
                if show {
                    display.present().await;
                }
            }
            Either4::Second(value) => {
                host_brightness = value;
            }
            Either4::Third(()) => {
                test_mode = !test_mode;
                paint_local = true; // react on toggle, not the next tick
            }
            Either4::Fourth(()) => {
                frame = frame.wrapping_add(1);
                paint_local = true;
            }
        }

        if paint_local {
            let connected = last_frame_at.is_some_and(|t| t.elapsed() < CONNECT_TIMEOUT);
            if last_frame_at.is_some() && !connected {
                // Stream silence means the host is gone, so disarm button
                // reporting until the next Hello. Guarded on `is_some` so a
                // host that has handshaken but not yet streamed isn't
                // disarmed prematurely.
                BUTTON_REPORTING.store(false, Ordering::Relaxed);
            }
            if test_mode {
                display.set_brightness(255);
                screens::paint_test(&mut display, FW_VERSION);
                display.present().await;
            } else if !connected {
                display.set_brightness(255);
                screens::paint_fallback(&mut display, frame);
                display.present().await;
            }
            // else: the stream owns the panel; ticks do nothing.
        }
    }
}
