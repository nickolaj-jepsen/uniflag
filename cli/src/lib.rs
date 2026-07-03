//! uniflag-cli — library layer of the binary-protocol bring-up CLI.
//!
//! Everything that must be testable without a serial port lives here:
//! deterministic test-pattern generators ([`patterns`]), the
//! monotonic-deadline pacing arithmetic ([`pacing`]), the inbound
//! COBS/CRC decode pipeline ([`rx`]), and the wire-byte builders shared
//! by the emit / stream / loopback modes ([`wire`]). The binary in
//! `main.rs` is a thin clap + serialport front-end over these modules.

pub mod pacing;
pub mod patterns;
pub mod rx;
pub mod wire;
