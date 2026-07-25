//! Golden-vector conformance for the CLI's RX path.
//!
//! The stream mode's inbound decoder must recover the device→host golden
//! vectors (`hello_ack`, both `button_event`s), reject every negative
//! vector with the right drop class, and walk `resync.stream` recovering
//! exactly the two packets embedded in it.

use std::fs;
use std::path::{Path, PathBuf};

use proto::packet::PROTOCOL_VERSION;
use uniflag_cli::rx::{Decoder, DropReason, OwnedPacket, RxEvent};

fn testdata_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("testdata")
        .join("proto")
}

fn read_vector(name: &str) -> Vec<u8> {
    let path = testdata_dir().join(name);
    fs::read(&path).unwrap_or_else(|err| {
        panic!(
            "missing golden vector {} ({err}); regenerate via just golden-regen",
            path.display()
        )
    })
}

fn decode_all(bytes: &[u8]) -> Vec<RxEvent> {
    let mut decoder = Decoder::default();
    decoder.feed(bytes)
}

#[test]
fn button_event_short_decodes_to_button_0_short_press() {
    assert_eq!(
        decode_all(&read_vector("button_event_short.wire")),
        vec![RxEvent::Packet(OwnedPacket::ButtonEvent {
            button: 0,
            kind: 0
        })]
    );
}

#[test]
fn button_event_long_decodes_to_button_2_long_press() {
    assert_eq!(
        decode_all(&read_vector("button_event_long.wire")),
        vec![RxEvent::Packet(OwnedPacket::ButtonEvent {
            button: 2,
            kind: 1
        })]
    );
}

#[test]
fn hello_ack_decodes_to_the_expected_packet() {
    assert_eq!(
        decode_all(&read_vector("hello_ack.wire")),
        vec![RxEvent::Packet(OwnedPacket::HelloAck {
            protocol_version: PROTOCOL_VERSION,
            width: 32,
            height: 32,
            fw_version: b"2.0.0-test".to_vec(),
        })]
    );
}

/// `resync.stream` is garbage (no delimiter), a lone `0x00`, then
/// hello.wire and brightness.wire verbatim; the README's
/// `expected_packets` are `hello` then `brightness`. The garbage segment
/// dies in the COBS layer; the survivors must match exactly and in order.
#[test]
fn resync_stream_survivors_are_exactly_hello_then_brightness() {
    let expected = vec![
        RxEvent::Dropped(DropReason::CobsMalformed),
        RxEvent::Packet(OwnedPacket::Hello {
            protocol_version: PROTOCOL_VERSION,
        }),
        RxEvent::Packet(OwnedPacket::Brightness { value: 200 }),
    ];
    let stream = read_vector("resync.stream");
    assert_eq!(decode_all(&stream), expected);

    // Byte-at-a-time delivery reassembles to the identical event stream.
    let mut trickle = Decoder::default();
    let mut events = Vec::new();
    for &byte in &stream {
        events.extend(trickle.feed(&[byte]));
    }
    assert_eq!(events, expected);
}

#[test]
fn negative_vectors_drop_with_the_contract_error_class() {
    let cases: &[(&str, DropReason)] = &[
        ("bad_crc.wire", DropReason::BadCrc),
        ("truncated.wire", DropReason::CobsMalformed),
        ("wrong_length_known_type.wire", DropReason::BadLength),
    ];
    for &(name, reason) in cases {
        assert_eq!(
            decode_all(&read_vector(name)),
            vec![RxEvent::Dropped(reason)],
            "{name}"
        );
    }

    // embedded_zero_garbage.wire has a 0x00 *inside* the COBS body. The
    // README's negative reading (strip one trailing delimiter, decode
    // the rest as one packet) sees one malformed packet; a streaming
    // decoder legitimately splits on the interior zero too and drops two
    // malformed segments. Either way nothing decodes.
    let events = decode_all(&read_vector("embedded_zero_garbage.wire"));
    assert!(!events.is_empty());
    assert!(
        events
            .iter()
            .all(|e| *e == RxEvent::Dropped(DropReason::CobsMalformed)),
        "embedded_zero_garbage: {events:?}"
    );
}

/// The unassigned-type boundary vector surfaces as Unknown — callers
/// ignore it, never treat it as an error (forward compat).
#[test]
fn cobs_boundary_254_surfaces_as_unknown_type() {
    assert_eq!(
        decode_all(&read_vector("cobs_boundary_254.wire")),
        vec![RxEvent::Unknown { ty: 0x7E }]
    );
}
