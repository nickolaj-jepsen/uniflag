//! uniflag v2 wire protocol — the frozen binary packet layer shared by the
//! firmware, `uniflag-cli`, and (as a C# mirror in `plugin/core/Protocol/`)
//! the SimHub plugin.
//!
//! The crate is `no_std` and allocation-free. Three modules:
//!
//! - [`packet`] — typed packet layer (Hello/HelloAck handshake, 3072-byte
//!   RGB888 `Frame`, `Brightness`, `ButtonEvent`), plus the shared
//!   constants: [`packet::PROTOCOL_VERSION`], the USB identity
//!   ([`packet::USB_VID`] / [`packet::USB_PID`]), and the panel geometry.
//! - [`cobs`] — COBS framing (encoded bytes never contain `0x00`; one
//!   trailing `0x00` delimiter per packet).
//! - [`crc`] — CRC-16/CCITT-FALSE over the raw bytes before COBS.
//!
//! The wire format is specified in prose by `docs/protocol.md` and in
//! bytes by the golden vectors under `testdata/proto/` (exercised by
//! `tests/golden_vectors.rs` here and by the plugin's conformance suite).
//! The packet set is **frozen**: any wire-visible change bumps
//! [`packet::PROTOCOL_VERSION`] and regenerates the vectors via
//! `just golden-regen` in a deliberate, reviewed commit.
//!
//! Forward-compat posture: receivers ignore unknown packet types, drop
//! bad-CRC / wrong-length packets silently, and resynchronize at the next
//! `0x00` delimiter.

#![no_std]

pub mod cobs;
pub mod crc;
pub mod packet;
