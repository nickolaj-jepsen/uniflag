//! Binary packet layer for the v2 protocol.
//!
//! Wire format, outermost first:
//!
//! ```text
//! wire   := cobs(raw) 0x00          -- one trailing delimiter per packet
//! raw    := type payload crc16      -- crc16 little-endian, over type+payload
//! type   := u8                      -- see PacketType
//! ```
//!
//! CRC parameters live in [`crate::crc`] (CRC-16/CCITT-FALSE). The CRC is
//! computed over the raw bytes *before* COBS so it also guards against
//! COBS-layer bugs, and appended little-endian.
//!
//! Forward-compat posture (carried over from the ASCII protocol): a
//! receiver **ignores packets with unknown type bytes** and **drops
//! packets with bad CRCs**, resynchronizing on the next `0x00` delimiter.
//!
//! Payload schemas: `Frame` is defined here (M2b); the remaining payloads
//! are frozen in M6 alongside the cross-language golden vectors.

use crate::{cobs, crc};

/// Protocol version carried in the `HelloAck` handshake (M6). Bump on any
/// wire-visible change after the M6 freeze.
pub const PROTOCOL_VERSION: u8 = 1;

pub const PANEL_WIDTH: usize = 32;
pub const PANEL_HEIGHT: usize = 32;

/// `Frame` payload: RGB888, row-major from the top-left, 3 bytes per pixel.
pub const FRAME_PAYLOAD_LEN: usize = PANEL_WIDTH * PANEL_HEIGHT * 3;

/// Largest raw packet: type byte + Frame payload + CRC.
pub const MAX_RAW_LEN: usize = 1 + FRAME_PAYLOAD_LEN + 2;

/// Largest on-the-wire packet, including the trailing `0x00` delimiter.
/// Sizes the firmware RX accumulator.
pub const MAX_WIRE_LEN: usize = cobs::max_encoded_len(MAX_RAW_LEN) + 1;

/// Packet type bytes. Host→device types have the high bit clear,
/// device→host types have it set.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum PacketType {
    Hello = 0x01,
    Frame = 0x02,
    Brightness = 0x03,
    HelloAck = 0x81,
    ButtonEvent = 0x82,
}

impl PacketType {
    pub fn from_byte(byte: u8) -> Option<Self> {
        match byte {
            0x01 => Some(Self::Hello),
            0x02 => Some(Self::Frame),
            0x03 => Some(Self::Brightness),
            0x81 => Some(Self::HelloAck),
            0x82 => Some(Self::ButtonEvent),
            _ => None,
        }
    }

    pub fn to_byte(self) -> u8 {
        self as u8
    }
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Error {
    /// Output (or scratch) buffer too small.
    BufferTooSmall,
    /// Raw packet shorter than type + CRC.
    TooShort,
    /// CRC mismatch — drop the packet and resync.
    BadCrc,
    /// COBS layer rejected the bytes.
    Cobs(cobs::Error),
}

/// Serialize the raw form (type + payload + CRC-LE) into `out`; returns
/// the raw length.
pub fn write_raw(ty: PacketType, payload: &[u8], out: &mut [u8]) -> Result<usize, Error> {
    let raw_len = 1 + payload.len() + 2;
    if out.len() < raw_len {
        return Err(Error::BufferTooSmall);
    }
    out[0] = ty.to_byte();
    out[1..1 + payload.len()].copy_from_slice(payload);
    let checksum = crc::update(crc::update(crc::INIT, &out[..1]), payload);
    out[1 + payload.len()..raw_len].copy_from_slice(&checksum.to_le_bytes());
    Ok(raw_len)
}

/// Full wire encode: raw form into `scratch`, COBS into `wire`, trailing
/// `0x00` delimiter appended. Returns the wire length. `scratch` needs
/// [`MAX_RAW_LEN`] bytes for a Frame (or `1 + payload + 2` generally);
/// `wire` needs [`MAX_WIRE_LEN`].
pub fn encode(
    ty: PacketType,
    payload: &[u8],
    scratch: &mut [u8],
    wire: &mut [u8],
) -> Result<usize, Error> {
    let raw_len = write_raw(ty, payload, scratch)?;
    let enc_len = cobs::encode(&scratch[..raw_len], wire).map_err(Error::Cobs)?;
    if enc_len >= wire.len() {
        return Err(Error::BufferTooSmall);
    }
    wire[enc_len] = 0x00;
    Ok(enc_len + 1)
}

/// Validate a COBS-decoded raw packet and split it into (type byte,
/// payload). The type byte is returned raw so callers can ignore unknown
/// types explicitly (forward compat) rather than erroring here.
pub fn parse_raw(raw: &[u8]) -> Result<(u8, &[u8]), Error> {
    if raw.len() < 3 {
        return Err(Error::TooShort);
    }
    let (body, crc_bytes) = raw.split_at(raw.len() - 2);
    let expected = u16::from_le_bytes([crc_bytes[0], crc_bytes[1]]);
    if crc::checksum(body) != expected {
        return Err(Error::BadCrc);
    }
    Ok((body[0], &body[1..]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn constants_are_stable() {
        // Wire-frozen numbers; changing any of these is a protocol break.
        assert_eq!(FRAME_PAYLOAD_LEN, 3072);
        assert_eq!(MAX_RAW_LEN, 3075);
        assert_eq!(MAX_WIRE_LEN, 3089);
        assert_eq!(PacketType::Hello.to_byte(), 0x01);
        assert_eq!(PacketType::Frame.to_byte(), 0x02);
        assert_eq!(PacketType::Brightness.to_byte(), 0x03);
        assert_eq!(PacketType::HelloAck.to_byte(), 0x81);
        assert_eq!(PacketType::ButtonEvent.to_byte(), 0x82);
    }

    #[test]
    fn type_bytes_round_trip() {
        for ty in [
            PacketType::Hello,
            PacketType::Frame,
            PacketType::Brightness,
            PacketType::HelloAck,
            PacketType::ButtonEvent,
        ] {
            assert_eq!(PacketType::from_byte(ty.to_byte()), Some(ty));
        }
        assert_eq!(PacketType::from_byte(0x00), None);
        assert_eq!(PacketType::from_byte(0x7F), None);
    }

    #[test]
    fn frame_round_trips_through_the_full_stack() {
        let payload: [u8; FRAME_PAYLOAD_LEN] = core::array::from_fn(|i| (i % 256) as u8);
        let mut scratch = [0u8; MAX_RAW_LEN];
        let mut wire = [0u8; MAX_WIRE_LEN];
        let wire_len =
            encode(PacketType::Frame, &payload, &mut scratch, &mut wire).expect("encode");
        assert!(wire_len <= MAX_WIRE_LEN);
        assert_eq!(wire[wire_len - 1], 0x00);
        assert!(!wire[..wire_len - 1].contains(&0));

        let mut raw = [0u8; MAX_RAW_LEN];
        let raw_len = cobs::decode(&wire[..wire_len - 1], &mut raw).expect("cobs decode");
        let (ty, got) = parse_raw(&raw[..raw_len]).expect("parse");
        assert_eq!(ty, PacketType::Frame.to_byte());
        assert_eq!(got, payload);
    }

    #[test]
    fn empty_payload_packet_round_trips() {
        // Hello carries no payload in the spike.
        let mut scratch = [0u8; 8];
        let mut wire = [0u8; 16];
        let wire_len = encode(PacketType::Hello, &[], &mut scratch, &mut wire).expect("encode");
        let mut raw = [0u8; 8];
        let raw_len = cobs::decode(&wire[..wire_len - 1], &mut raw).expect("cobs decode");
        let (ty, payload) = parse_raw(&raw[..raw_len]).expect("parse");
        assert_eq!(ty, PacketType::Hello.to_byte());
        assert!(payload.is_empty());
    }

    #[test]
    fn corrupted_bytes_fail_crc() {
        let payload = [0xAAu8; 16];
        let mut raw = [0u8; 32];
        let raw_len = write_raw(PacketType::Brightness, &payload, &mut raw).expect("write");
        assert!(parse_raw(&raw[..raw_len]).is_ok());
        for i in 0..raw_len {
            raw[i] ^= 0x01;
            assert_eq!(
                parse_raw(&raw[..raw_len]),
                Err(Error::BadCrc),
                "flip at byte {i} not caught"
            );
            raw[i] ^= 0x01;
        }
    }

    #[test]
    fn truncated_raw_is_rejected() {
        assert_eq!(parse_raw(&[]), Err(Error::TooShort));
        assert_eq!(parse_raw(&[0x02]), Err(Error::TooShort));
        assert_eq!(parse_raw(&[0x02, 0x00]), Err(Error::TooShort));
        // Exactly type + CRC (empty payload) is the minimum valid size.
        let mut raw = [0u8; 3];
        let n = write_raw(PacketType::Hello, &[], &mut raw).expect("write");
        assert_eq!(n, 3);
        assert!(parse_raw(&raw[..n]).is_ok());
    }

    #[test]
    fn unknown_type_is_parseable_for_caller_side_ignoring() {
        // A valid-CRC packet with an unassigned type byte parses fine —
        // ignoring it is the caller's job (forward compat).
        let mut raw = [0u8; 8];
        raw[0] = 0x7E;
        raw[1] = 0x42;
        let checksum = crc::checksum(&raw[..2]);
        raw[2..4].copy_from_slice(&checksum.to_le_bytes());
        let (ty, payload) = parse_raw(&raw[..4]).expect("parse");
        assert_eq!(ty, 0x7E);
        assert_eq!(payload, &[0x42]);
        assert_eq!(PacketType::from_byte(ty), None);
    }
}
