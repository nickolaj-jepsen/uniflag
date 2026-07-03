//! Wire-byte builders shared by the emit, stream, and loopback modes.
//!
//! Thin wrappers over [`proto::packet::Packet::encode`] that allocate the
//! right-sized buffers and hand back owned byte vectors, plus the
//! loopback stream generator. All host→device bytes the CLI ever sends
//! originate here, so pinning these functions against the golden vectors
//! pins every mode at once.

use anyhow::{anyhow, Result};
use proto::packet::{Packet, FRAME_PAYLOAD_LEN, MAX_RAW_LEN, MAX_WIRE_LEN, PROTOCOL_VERSION};

use crate::patterns::Pattern;

/// Encode one typed packet to its on-the-wire form (COBS + trailing
/// `0x00` delimiter).
pub fn encode_packet(packet: &Packet<'_>) -> Result<Vec<u8>> {
    let mut scratch = vec![0u8; MAX_RAW_LEN];
    let mut wire = vec![0u8; MAX_WIRE_LEN];
    let len = packet
        .encode(&mut scratch, &mut wire)
        .map_err(|err| anyhow!("packet encode failed: {err:?}"))?;
    wire.truncate(len);
    Ok(wire)
}

/// `Hello` carrying the host's [`PROTOCOL_VERSION`].
pub fn hello() -> Result<Vec<u8>> {
    encode_packet(&Packet::Hello {
        protocol_version: PROTOCOL_VERSION,
    })
}

/// `Brightness` set-point.
pub fn brightness(value: u8) -> Result<Vec<u8>> {
    encode_packet(&Packet::Brightness { value })
}

/// One `Frame` of `pattern` at `frame_index`.
pub fn frame(pattern: Pattern, frame_index: u64) -> Result<Vec<u8>> {
    let mut pixels = [0u8; FRAME_PAYLOAD_LEN];
    pattern.paint(frame_index, &mut pixels);
    frame_from_pixels(&pixels)
}

/// One `Frame` carrying `pixels` verbatim.
pub fn frame_from_pixels(pixels: &[u8; FRAME_PAYLOAD_LEN]) -> Result<Vec<u8>> {
    encode_packet(&Packet::Frame { pixels })
}

/// The exact byte stream the loopback mode decodes, in the same packet
/// order the stream mode sends on the wire: `Hello`, then `Brightness`
/// if given, then `frames` Frame packets of `pattern` — each preceded by
/// a per-frame `Brightness` when the pattern sweeps brightness.
///
/// `Hello` is deliberately **included**: loopback verifies the whole TX
/// byte stream, and the handshake open is part of it. `HelloAck` is a
/// device-side reply, so it has no place in a host-emitted stream.
///
/// Returns the bytes and the number of packets they encode, so callers
/// can assert that every self-emitted packet survives decoding.
pub fn loopback_stream(
    pattern: Pattern,
    frames: u64,
    brightness_value: Option<u8>,
) -> Result<(Vec<u8>, usize)> {
    let mut bytes = Vec::new();
    let mut packets = 0usize;

    bytes.extend_from_slice(&hello()?);
    packets += 1;
    if let Some(value) = brightness_value {
        bytes.extend_from_slice(&brightness(value)?);
        packets += 1;
    }
    for frame_index in 0..frames {
        if let Some(value) = pattern.brightness_for_frame(frame_index) {
            bytes.extend_from_slice(&brightness(value)?);
            packets += 1;
        }
        bytes.extend_from_slice(&frame(pattern, frame_index)?);
        packets += 1;
    }
    Ok((bytes, packets))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::patterns::Rgb;
    use crate::rx::{Decoder, OwnedPacket, RxEvent};

    #[test]
    fn every_wire_packet_ends_in_exactly_one_delimiter() {
        let cases = [
            hello().expect("hello"),
            brightness(0).expect("brightness"),
            frame(Pattern::Gradient, 0).expect("frame"),
        ];
        for bytes in cases {
            assert_eq!(bytes.last(), Some(&0x00));
            assert!(!bytes[..bytes.len() - 1].contains(&0x00));
        }
    }

    #[test]
    fn loopback_stream_decodes_to_exactly_its_own_packet_count() {
        let (bytes, expected) =
            loopback_stream(Pattern::BrightnessSweep, 3, Some(128)).expect("stream");
        // Hello + initial Brightness + 3 × (sweep Brightness + Frame).
        assert_eq!(expected, 8);

        let mut decoder = Decoder::default();
        let events = decoder.feed(&bytes);
        assert_eq!(events.len(), expected);
        assert!(events
            .iter()
            .all(|event| matches!(event, RxEvent::Packet(_))));
        assert!(matches!(
            &events[0],
            RxEvent::Packet(OwnedPacket::Hello { .. })
        ));
        assert!(matches!(
            &events[1],
            RxEvent::Packet(OwnedPacket::Brightness { value: 128 })
        ));
    }

    #[test]
    fn frame_payload_survives_the_round_trip_byte_exactly() {
        let pattern = Pattern::Solid(Rgb { r: 1, g: 0, b: 255 });
        for frame_index in [0u64, 7] {
            let bytes = frame(pattern, frame_index).expect("encode");
            let mut decoder = Decoder::default();
            let events = decoder.feed(&bytes);
            let [RxEvent::Packet(OwnedPacket::Frame { pixels })] = events.as_slice() else {
                panic!("expected exactly one decoded Frame, got {events:?}");
            };
            let mut expected = [0u8; FRAME_PAYLOAD_LEN];
            pattern.paint(frame_index, &mut expected);
            assert_eq!(pixels.as_slice(), expected.as_slice());
        }
    }
}
