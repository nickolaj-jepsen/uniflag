//! Inbound decode pipeline: a `0x00`-delimited COBS byte stream in, typed
//! packets out.
//!
//! Mirrors the receiver posture in docs/protocol.md: malformed
//! COBS, bad CRCs, wrong-length known types, and oversized accumulations
//! are dropped and the decoder realigns at the next delimiter;
//! back-to-back delimiters are no-ops; CRC-valid packets with unassigned
//! type bytes surface as [`RxEvent::Unknown`] so callers can ignore them
//! explicitly (forward compat).

use proto::cobs;
use proto::packet::{self, Packet, Parsed, FRAME_PAYLOAD_LEN, MAX_WIRE_LEN};

/// Owned mirror of [`proto::packet::Packet`] (which borrows its payload
/// from the receive buffer) so decoded packets outlive the feed call.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum OwnedPacket {
    Hello {
        protocol_version: u8,
    },
    Frame {
        pixels: Box<[u8; FRAME_PAYLOAD_LEN]>,
    },
    Brightness {
        value: u8,
    },
    HelloAck {
        protocol_version: u8,
        width: u8,
        height: u8,
        fw_version: Vec<u8>,
    },
    ButtonEvent {
        button: u8,
        kind: u8,
    },
}

impl OwnedPacket {
    fn from_packet(packet: &Packet<'_>) -> Self {
        match *packet {
            Packet::Hello { protocol_version } => OwnedPacket::Hello { protocol_version },
            Packet::Frame { pixels } => OwnedPacket::Frame {
                pixels: Box::new(*pixels),
            },
            Packet::Brightness { value } => OwnedPacket::Brightness { value },
            Packet::HelloAck {
                protocol_version,
                width,
                height,
                fw_version,
            } => OwnedPacket::HelloAck {
                protocol_version,
                width,
                height,
                fw_version: fw_version.to_vec(),
            },
            Packet::ButtonEvent { button, kind } => OwnedPacket::ButtonEvent { button, kind },
        }
    }

    /// One-line human summary (frame payloads are elided, firmware
    /// version strings decoded lossily).
    pub fn summary(&self) -> String {
        match self {
            OwnedPacket::Hello { protocol_version } => {
                format!("Hello {{ protocol_version: {protocol_version} }}")
            }
            OwnedPacket::Frame { .. } => format!("Frame {{ {FRAME_PAYLOAD_LEN} B RGB888 }}"),
            OwnedPacket::Brightness { value } => format!("Brightness {{ value: {value} }}"),
            OwnedPacket::HelloAck {
                protocol_version,
                width,
                height,
                fw_version,
            } => format!(
                "HelloAck {{ protocol_version: {protocol_version}, panel: {width}x{height}, \
                 fw_version: \"{}\" }}",
                String::from_utf8_lossy(fw_version)
            ),
            OwnedPacket::ButtonEvent { button, kind } => {
                format!("ButtonEvent {{ button: {button}, kind: {kind} }}")
            }
        }
    }
}

/// Why a delimiter-to-delimiter segment was dropped.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum DropReason {
    /// COBS layer rejected the segment (embedded zero, truncated group,
    /// or a decode larger than any legal packet).
    CobsMalformed,
    /// CRC mismatch over type + payload.
    BadCrc,
    /// Known type with a payload length that doesn't match its layout.
    BadLength,
    /// Decoded to fewer bytes than type + CRC.
    TooShort,
    /// Accumulated more than [`MAX_WIRE_LEN`] bytes without a delimiter;
    /// everything up to the next delimiter was discarded.
    Oversized,
}

/// What one delimiter-terminated segment produced.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RxEvent {
    /// A CRC-valid packet of a known type.
    Packet(OwnedPacket),
    /// A CRC-valid packet with an unassigned type byte — ignore it
    /// (forward compat); surfaced so loopback accounting stays exact.
    Unknown { ty: u8 },
    /// The segment failed the decode pipeline and was dropped.
    Dropped(DropReason),
}

/// Streaming decoder: accumulates bytes, splits on `0x00` delimiters, and
/// runs each complete segment through COBS decode → CRC check → typed
/// parse. Feed it whatever chunk sizes the transport hands you.
#[derive(Default)]
pub struct Decoder {
    pending: Vec<u8>,
    overflowed: bool,
}

impl Decoder {
    /// Consume `bytes`, returning one event per completed segment (in
    /// stream order). Partial segments stay buffered for the next feed.
    pub fn feed(&mut self, bytes: &[u8]) -> Vec<RxEvent> {
        let mut events = Vec::new();
        for &byte in bytes {
            if byte == 0x00 {
                if self.overflowed {
                    events.push(RxEvent::Dropped(DropReason::Oversized));
                } else if !self.pending.is_empty() {
                    events.push(decode_segment(&self.pending));
                }
                // Empty delimiter-to-delimiter spans are no-ops.
                self.pending.clear();
                self.overflowed = false;
            } else if !self.overflowed {
                self.pending.push(byte);
                if self.pending.len() >= MAX_WIRE_LEN {
                    // Longer than any legal packet: drop until the next
                    // delimiter realigns us.
                    self.pending.clear();
                    self.overflowed = true;
                }
            }
        }
        events
    }
}

/// Decode pipeline for one delimiter-stripped segment.
fn decode_segment(segment: &[u8]) -> RxEvent {
    // COBS never expands on decode, so a segment capped below
    // MAX_WIRE_LEN always fits (garbage may still out-size MAX_RAW_LEN).
    let mut raw = [0u8; MAX_WIRE_LEN];
    let raw_len = match cobs::decode(segment, &mut raw) {
        Ok(n) => n,
        Err(_) => return RxEvent::Dropped(DropReason::CobsMalformed),
    };
    match packet::parse_packet(&raw[..raw_len]) {
        Ok(Parsed::Known(packet)) => RxEvent::Packet(OwnedPacket::from_packet(&packet)),
        Ok(Parsed::Unknown { ty, .. }) => RxEvent::Unknown { ty },
        Err(packet::Error::BadCrc) => RxEvent::Dropped(DropReason::BadCrc),
        Err(packet::Error::BadLength) => RxEvent::Dropped(DropReason::BadLength),
        Err(packet::Error::TooShort) => RxEvent::Dropped(DropReason::TooShort),
        // parse_packet only returns the three errors above, but stay
        // total: anything new is a drop, same as the firmware posture.
        Err(_) => RxEvent::Dropped(DropReason::CobsMalformed),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wire;

    #[test]
    fn split_feeds_reassemble_across_chunk_boundaries() {
        let bytes = wire::brightness(200).expect("encode");
        let mut whole = Decoder::default();
        let expected = whole.feed(&bytes);
        assert_eq!(
            expected,
            vec![RxEvent::Packet(OwnedPacket::Brightness { value: 200 })]
        );

        // Byte-at-a-time produces the identical event sequence.
        let mut trickle = Decoder::default();
        let mut events = Vec::new();
        for &byte in &bytes {
            events.extend(trickle.feed(&[byte]));
        }
        assert_eq!(events, expected);
    }

    #[test]
    fn back_to_back_delimiters_are_no_ops() {
        let mut decoder = Decoder::default();
        assert_eq!(decoder.feed(&[0x00, 0x00, 0x00]), Vec::new());
    }

    #[test]
    fn oversized_accumulation_drops_until_the_next_delimiter() {
        let mut decoder = Decoder::default();
        // More delimiter-less non-zero bytes than any legal packet.
        let garbage = vec![0x42u8; MAX_WIRE_LEN + 512];
        assert_eq!(decoder.feed(&garbage), Vec::new());
        // The delimiter flushes exactly one Oversized drop, then a valid
        // packet decodes cleanly — the decoder realigned.
        let mut tail = vec![0x00];
        tail.extend(wire::hello().expect("encode"));
        assert_eq!(
            decoder.feed(&tail),
            vec![
                RxEvent::Dropped(DropReason::Oversized),
                RxEvent::Packet(OwnedPacket::Hello {
                    protocol_version: proto::packet::PROTOCOL_VERSION
                }),
            ]
        );
    }
}
