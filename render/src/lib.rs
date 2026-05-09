//! `uniflag-render` — host-testable rendering primitives shared between the
//! firmware and host-side tests.
//!
//! This crate is `no_std` and depends only on [`proto`]. It owns:
//!
//! - [`anim`] — integer-only animation primitives (sine LUT, strobe envelope,
//!   breathing modulator, cloth-wave overlay, RGB scaling).
//! - [`surface`] — the [`Surface`](surface::Surface) trait that paint
//!   functions write to.
//! - [`effects`] — per-flag paint functions (`paint`, `paint_yellow`,
//!   `paint_red`, …) generic over any [`Surface`].
//!
//! Host integration tests in `render/tests/` provide their own
//! `MockSurface` (see `tests/common/mod.rs`) — keeping it test-side avoids
//! pulling `alloc` into firmware.

#![no_std]

pub mod anim;
pub mod effects;
pub mod surface;

pub use surface::{Rgb, Surface};

/// Panel width in pixels. Both the Cosmic Unicorn hardware and the host
/// `MockSurface` agree on this.
pub const WIDTH: usize = 32;
/// Panel height in pixels.
pub const HEIGHT: usize = 32;
