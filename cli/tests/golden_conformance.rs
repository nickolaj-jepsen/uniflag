//! Golden-vector conformance for the CLI's TX path (docs/v2-plan.md M7).
//!
//! The bytes `uniflag-cli emit` writes must equal the frozen vectors
//! under `testdata/proto/` byte-for-byte — that is what makes shell-level
//! byte-diff verification meaningful. Path discovery mirrors
//! `proto/tests/golden_vectors.rs`: the fixtures live at the repo top
//! level so every language's suite reaches them by relative path.

use std::fs;
use std::path::{Path, PathBuf};

use proto::packet::FRAME_PAYLOAD_LEN;
use uniflag_cli::patterns::{Pattern, Rgb};
use uniflag_cli::rx::{Decoder, OwnedPacket, RxEvent};
use uniflag_cli::wire;

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

/// Byte-exact comparison with the offset of the first difference instead
/// of a multi-kilobyte hex dump.
fn assert_bytes_eq(context: &str, got: &[u8], want: &[u8]) {
    if got == want {
        return;
    }
    let offset = got
        .iter()
        .zip(want.iter())
        .position(|(a, b)| a != b)
        .unwrap_or(got.len().min(want.len()));
    panic!(
        "{context}: byte mismatch - got {} bytes, want {} bytes, first difference at \
         offset {offset} (got {:?}, want {:?})",
        got.len(),
        want.len(),
        got.get(offset),
        want.get(offset),
    );
}

#[test]
fn emit_hello_matches_the_golden_wire_vector() {
    let bytes = wire::hello().expect("encode hello");
    assert_bytes_eq(
        "emit(hello) vs hello.wire",
        &bytes,
        &read_vector("hello.wire"),
    );
}

#[test]
fn emit_brightness_200_matches_the_golden_wire_vector() {
    let bytes = wire::brightness(200).expect("encode brightness");
    assert_bytes_eq(
        "emit(brightness 200) vs brightness.wire",
        &bytes,
        &read_vector("brightness.wire"),
    );
}

/// The golden frame payload is the M6 piecewise COBS-stress pattern, not
/// one of the CLI's visual test patterns — so the frame TX path is pinned
/// by feeding the golden payload (frame.raw[1..3073]) through the CLI's
/// encoder and requiring the golden wire bytes back. That exercises the
/// exact encode pipeline `emit frame` uses, on the exact frozen payload.
#[test]
fn frame_encode_path_reproduces_the_golden_frame_wire() {
    let raw = read_vector("frame.raw");
    assert_eq!(raw.len(), 1 + FRAME_PAYLOAD_LEN + 2, "frame.raw layout");
    let payload: &[u8; FRAME_PAYLOAD_LEN] = raw[1..1 + FRAME_PAYLOAD_LEN]
        .try_into()
        .expect("payload slice is exactly FRAME_PAYLOAD_LEN");

    let bytes = wire::frame_from_pixels(payload).expect("encode frame");
    assert_bytes_eq(
        "frame_from_pixels(golden payload) vs frame.wire",
        &bytes,
        &read_vector("frame.wire"),
    );
}

/// Every CLI pattern's emitted frame survives the full proto decode
/// pipeline with its payload byte-exact — the loopback guarantee, pinned
/// per pattern.
#[test]
fn every_pattern_frame_round_trips_byte_exactly() {
    let patterns = [
        Pattern::Solid(Rgb {
            r: 255,
            g: 128,
            b: 0,
        }),
        Pattern::Gradient,
        Pattern::MovingPixel,
        Pattern::Checkerboard,
        Pattern::BrightnessSweep,
    ];
    for pattern in patterns {
        for frame_index in [0u64, 1, 500] {
            let bytes = wire::frame(pattern, frame_index).expect("encode");
            let mut decoder = Decoder::default();
            let events = decoder.feed(&bytes);
            let [RxEvent::Packet(OwnedPacket::Frame { pixels })] = events.as_slice() else {
                panic!(
                    "{pattern:?} frame {frame_index}: expected one decoded Frame, got {events:?}"
                );
            };
            let mut expected = [0u8; FRAME_PAYLOAD_LEN];
            pattern.paint(frame_index, &mut expected);
            assert_bytes_eq(
                &format!("{pattern:?} frame {frame_index} payload round trip"),
                pixels.as_slice(),
                &expected,
            );
        }
    }
}
