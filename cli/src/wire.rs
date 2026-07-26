//! Wire-byte builders for the stream mode.
//!
//! Thin wrappers over [`proto::packet::Packet::encode`] that allocate the
//! right-sized buffers and hand back owned byte vectors. All host→device
//! bytes the CLI ever sends originate here.

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
    encode_packet(&Packet::Frame { pixels: &pixels })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::patterns::Rgb;
    use crate::rx::{Decoder, OwnedPacket, RxEvent};

    /// The exact byte stream the stream mode sends, and its packet count.
    /// `Hello` is deliberately included — the handshake open is part of
    /// the TX stream; `HelloAck` is a device reply, so it is not.
    fn tx_stream(
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

    /// TX bytes and RX pipeline are two statements of the same wire
    /// format; this is where they are checked against each other.
    #[test]
    fn the_whole_tx_stream_decodes_to_exactly_its_own_packet_count() {
        let (bytes, expected) = tx_stream(Pattern::BrightnessSweep, 3, Some(128)).expect("stream");
        // Hello + initial Brightness + 3 × (sweep Brightness + Frame).
        assert_eq!(expected, 8);

        // Transport-sized chunks so the accumulator paths get real work.
        let mut decoder = Decoder::default();
        let mut events = Vec::new();
        for chunk in bytes.chunks(1024) {
            events.extend(decoder.feed(chunk));
        }
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
