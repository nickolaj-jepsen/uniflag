//! `uniflag-render` — host-testable rendering primitives shared between the
//! firmware and host-side tests.
//!
//! This crate is `no_std` and depends only on [`proto`]. It owns:
//!
//! - [`anim`] — integer-only animation primitives (sine LUT, strobe envelope,
//!   breathing modulator, cloth-wave overlay, RGB scaling).
//!
//! Future modules (a `Surface` trait, the `effects` paint functions, and the
//! `BrightnessController` state machine) will land here too so that the
//! firmware can stay a thin hardware adapter and host tests can cover the
//! actual rendering and brightness logic.

#![no_std]

pub mod anim;

/// Panel width in pixels. Both the Cosmic Unicorn hardware and the host
/// `MockSurface` agree on this.
pub const WIDTH: usize = 32;
/// Panel height in pixels.
pub const HEIGHT: usize = 32;
