//! uniflag v2 wire protocol — the binary packet layer shared by the
//! firmware, `uniflag-cli`, and (as a C# mirror in `plugin/core/Protocol/`)
//! the SimHub plugin. `no_std` and allocation-free.
//!
//! Written up in prose by `docs/protocol.md` and in bytes by the golden
//! vectors under `testdata/proto/`. The packet set is versioned rather than
//! fixed — change it when it's worth changing, but a wire-visible change
//! bumps [`packet::PROTOCOL_VERSION`] and regenerates the vectors via
//! `just golden-regen` in the same commit: firmware and plugin ship
//! separately and must never disagree about what a byte means.
//!
//! Forward-compat posture: receivers ignore unknown packet types, drop
//! bad-CRC / wrong-length packets silently, and resynchronize at the next
//! `0x00` delimiter.

#![no_std]

pub mod cobs;
pub mod crc;
pub mod packet;
