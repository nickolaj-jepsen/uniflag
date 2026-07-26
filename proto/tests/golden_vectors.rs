//! Cross-language golden-vector conformance suite.
//!
//! `testdata/proto/` (top-level, so the C# plugin suite reaches it by
//! relative path without entering a Rust crate) holds the committed byte
//! vectors. `testdata/proto/README.md` describes what each one is for;
//! it is hand-written, like `.gitattributes`. The tests here load the
//! exact committed files and assert:
//!
//! - `.raw` vectors (type + payload + CRC-16 LE) parse to the expected
//!   typed [`Packet`] and re-encode byte-identically;
//! - `.wire` vectors (COBS + trailing `0x00` delimiter) decode through
//!   COBS + parse to the same packet and re-encode byte-identically;
//! - negative vectors fail with exactly the expected error class;
//! - the resync stream recovers exactly the embedded valid packets.
//!
//! Regeneration: only via `just golden-regen`, which wraps
//! `cargo test -p proto --test golden_vectors -- --ignored regen`. The
//! [`regen`] test rewrites every *vector* file deterministically — no
//! wall clock, no randomness, fixed patterns and seeds only — so two
//! consecutive runs are byte-identical. It never touches the hand-written
//! `README.md` / `.gitattributes`.

use std::fs;
use std::path::{Path, PathBuf};

use proto::packet::{
    self, Button, Packet, PacketType, Parsed, PressKind, FRAME_PAYLOAD_LEN, MAX_RAW_LEN,
    MAX_WIRE_LEN, PANEL_HEIGHT, PANEL_WIDTH, PROTOCOL_VERSION,
};
use proto::{cobs, crc};

// Vector inputs. Changing anything here changes the golden files — regenerate
// via `just golden-regen`, and on purpose: a regen that moves bytes is a
// protocol change.

/// Brightness set-point carried by the `brightness` vector.
const BRIGHTNESS_VALUE: u8 = 200;

/// Firmware version string carried by the `hello_ack` vector.
const FW_VERSION: &[u8] = b"2.0.0-test";

/// Unassigned type byte used by the `cobs_boundary_254` vector — receivers
/// must parse it as [`Parsed::Unknown`] and ignore it.
const BOUNDARY_TYPE_BYTE: u8 = 0x7E;

/// Leading garbage of `resync.stream`: plausible line noise, deliberately
/// free of `0x00` so the first delimiter in the stream is the explicit one.
const RESYNC_GARBAGE: &[u8] = &[0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x13, 0x37];

/// One positive vector: its payload written out longhand — independently
/// of [`Packet::encode`], so the two statements of each layout cross-check
/// — and the typed packet it must parse to. Prose for each vector lives in
/// `testdata/proto/README.md`.
struct PositiveVector<'a> {
    name: &'static str,
    ty: PacketType,
    payload: Vec<u8>,
    packet: Packet<'a>,
}

fn positive_vectors(pixels: &[u8; FRAME_PAYLOAD_LEN]) -> Vec<PositiveVector<'_>> {
    let hello_ack_payload = {
        let mut payload = vec![PROTOCOL_VERSION, PANEL_WIDTH as u8, PANEL_HEIGHT as u8];
        payload.extend_from_slice(FW_VERSION);
        payload
    };
    vec![
        PositiveVector {
            name: "hello",
            ty: PacketType::Hello,
            payload: vec![PROTOCOL_VERSION],
            packet: Packet::Hello {
                protocol_version: PROTOCOL_VERSION,
            },
        },
        PositiveVector {
            name: "hello_ack",
            ty: PacketType::HelloAck,
            payload: hello_ack_payload,
            packet: Packet::HelloAck {
                protocol_version: PROTOCOL_VERSION,
                width: PANEL_WIDTH as u8,
                height: PANEL_HEIGHT as u8,
                fw_version: FW_VERSION,
            },
        },
        PositiveVector {
            name: "brightness",
            ty: PacketType::Brightness,
            payload: vec![BRIGHTNESS_VALUE],
            packet: Packet::Brightness {
                value: BRIGHTNESS_VALUE,
            },
        },
        PositiveVector {
            name: "button_event_short",
            ty: PacketType::ButtonEvent,
            payload: vec![Button::BrightnessUp.to_byte(), PressKind::Short.to_byte()],
            packet: Packet::ButtonEvent {
                button: Button::BrightnessUp.to_byte(),
                kind: PressKind::Short.to_byte(),
            },
        },
        PositiveVector {
            name: "button_event_long",
            ty: PacketType::ButtonEvent,
            payload: vec![Button::Sleep.to_byte(), PressKind::Long.to_byte()],
            packet: Packet::ButtonEvent {
                button: Button::Sleep.to_byte(),
                kind: PressKind::Long.to_byte(),
            },
        },
        PositiveVector {
            name: "frame",
            ty: PacketType::Frame,
            payload: pixels.to_vec(),
            packet: Packet::Frame { pixels },
        },
    ]
}

/// Error classes the negative vectors are allowed to name. These are the
/// cross-language contract classes — each implementation maps them onto
/// its own error type (Rust: `cobs::Error::Malformed`,
/// `packet::Error::BadCrc`, `packet::Error::BadLength`).
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
enum ErrorClass {
    CobsMalformed,
    BadCrc,
    BadLength,
}

struct NegativeVector {
    name: &'static str,
    class: ErrorClass,
    wire: Vec<u8>,
}

fn negative_vectors() -> Vec<NegativeVector> {
    let bad_crc = {
        let mut raw = raw_of(PacketType::Brightness, &[BRIGHTNESS_VALUE]);
        // Corrupt the payload byte after the CRC was computed. 0xC8 ^
        // 0xFF = 0x37 stays non-zero, so the COBS layer is untouched
        // and the corruption is visible only to the CRC check.
        raw[1] ^= 0xFF;
        wire_of_raw(&raw)
    };
    vec![
        NegativeVector {
            name: "bad_crc",
            class: ErrorClass::BadCrc,
            wire: bad_crc,
        },
        NegativeVector {
            name: "truncated",
            class: ErrorClass::CobsMalformed,
            // Group header 0x05 promises 4 data bytes; only 2 arrive
            // before the delimiter.
            wire: vec![0x05, 0x11, 0x22, 0x00],
        },
        NegativeVector {
            name: "embedded_zero_garbage",
            class: ErrorClass::CobsMalformed,
            // Group header 0x04 promises 3 data bytes and gets them, but
            // one is an embedded 0x00 — never produced by a valid encoder.
            wire: vec![0x04, 0x41, 0x00, 0x42, 0x00],
        },
        NegativeVector {
            name: "wrong_length_known_type",
            class: ErrorClass::BadLength,
            wire: wire_of_raw(&raw_of(PacketType::Frame, &[0x01, 0x02, 0x03, 0x04, 0x05])),
        },
    ]
}

// Deterministic builders — pure functions of the constants above.

/// Byte `i` of the 3072-byte frame payload. Piecewise so the framing layer
/// sees zero bytes, a long 0xFF run, and 254+ zero-free runs (the README
/// states the same formula in prose — keep in sync).
fn frame_pixel(i: usize) -> u8 {
    match i {
        0..=255 => {
            if i.is_multiple_of(8) {
                0
            } else {
                i as u8
            }
        }
        256..=511 => 0xFF,
        512..=1023 => ((i * 7) % 256) as u8,
        1024..=1341 => (i % 253) as u8 + 1,
        _ => (i % 256) as u8,
    }
}

fn frame_pixels() -> [u8; FRAME_PAYLOAD_LEN] {
    core::array::from_fn(frame_pixel)
}

/// Raw form (type + payload + CRC-16 LE) via the library encoder.
fn raw_of(ty: PacketType, payload: &[u8]) -> Vec<u8> {
    let mut out = vec![0u8; 1 + payload.len() + 2];
    let n = packet::write_raw(ty, payload, &mut out).expect("raw buffer sized exactly");
    out.truncate(n);
    out
}

/// Wire form: COBS(raw) plus the single trailing `0x00` delimiter.
fn wire_of_raw(raw: &[u8]) -> Vec<u8> {
    let mut out = vec![0u8; cobs::max_encoded_len(raw.len()) + 1];
    let n = cobs::encode(raw, &mut out).expect("wire buffer sized by max_encoded_len");
    out[n] = 0x00;
    out.truncate(n + 1);
    out
}

/// Raw form of the `cobs_boundary_254` vector: an unknown-type packet whose
/// raw bytes end in exactly 254 non-zero bytes (252 pattern bytes + 2 CRC
/// bytes) immediately after a `0x00`. The tweak byte deterministically
/// walks up from zero until neither CRC byte is zero, so the trailing run
/// is guaranteed zero-free; the first hit is what the committed file holds.
fn boundary_raw() -> Vec<u8> {
    for tweak in 0..=u8::MAX {
        let mut raw = vec![BOUNDARY_TYPE_BYTE, 0x51, tweak, 0x00];
        raw.extend(1..=252u8);
        let checksum = crc::checksum(&raw).to_le_bytes();
        if checksum[0] != 0 && checksum[1] != 0 {
            raw.extend_from_slice(&checksum);
            return raw;
        }
    }
    panic!("no tweak byte yields a zero-free CRC");
}

/// `resync.stream`: garbage (no delimiter), a lone `0x00`, then the exact
/// bytes of `hello.wire` and `brightness.wire` (same payloads as the
/// positive-vector table).
fn resync_stream() -> Vec<u8> {
    let mut stream = RESYNC_GARBAGE.to_vec();
    stream.push(0x00);
    stream.extend(wire_of_raw(&raw_of(PacketType::Hello, &[PROTOCOL_VERSION])));
    stream.extend(wire_of_raw(&raw_of(
        PacketType::Brightness,
        &[BRIGHTNESS_VALUE],
    )));
    stream
}

/// Every regenerable file, name → bytes: the single source [`regen`]
/// writes.
fn all_files() -> Vec<(String, Vec<u8>)> {
    let pixels = frame_pixels();
    let mut files: Vec<(String, Vec<u8>)> = Vec::new();
    for v in positive_vectors(&pixels) {
        let raw = raw_of(v.ty, &v.payload);
        files.push((format!("{}.wire", v.name), wire_of_raw(&raw)));
        files.push((format!("{}.raw", v.name), raw));
    }
    let braw = boundary_raw();
    files.push(("cobs_boundary_254.wire".to_string(), wire_of_raw(&braw)));
    files.push(("cobs_boundary_254.raw".to_string(), braw));
    for n in negative_vectors() {
        files.push((format!("{}.wire", n.name), n.wire));
    }
    files.push(("resync.stream".to_string(), resync_stream()));
    files
}

// Test plumbing.

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

/// Byte-exact comparison with a diff-friendly failure message (offset of
/// the first difference instead of a multi-kilobyte hex dump).
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

/// The receiver-side decode pipeline the negative vectors are defined
/// against: strip the single trailing delimiter, COBS-decode, CRC-check,
/// typed-parse. Collapses the Rust error types onto the cross-language
/// [`ErrorClass`] contract; anything outside the contract panics.
fn classify(name: &str, wire: &[u8]) -> Result<(), ErrorClass> {
    assert_eq!(
        wire.last(),
        Some(&0x00),
        "{name}: missing trailing delimiter"
    );
    let body = &wire[..wire.len() - 1];
    let mut raw = vec![0u8; MAX_RAW_LEN];
    let raw_len = match cobs::decode(body, &mut raw) {
        Ok(n) => n,
        Err(cobs::Error::Malformed) => return Err(ErrorClass::CobsMalformed),
        Err(err) => panic!("{name}: unexpected COBS error {err:?}"),
    };
    match packet::parse_packet(&raw[..raw_len]) {
        Ok(_) => Ok(()),
        Err(packet::Error::BadCrc) => Err(ErrorClass::BadCrc),
        Err(packet::Error::BadLength) => Err(ErrorClass::BadLength),
        Err(err) => panic!("{name}: unexpected parse error {err:?}"),
    }
}

// Normal tests — run against the committed files on disk.

#[test]
fn raw_vectors_parse_to_the_expected_typed_packet() {
    let pixels = frame_pixels();
    for v in positive_vectors(&pixels) {
        let raw = read_vector(&format!("{}.raw", v.name));
        // The longhand payload statement matches the committed bytes.
        assert_bytes_eq(
            &format!("{}.raw longhand payload", v.name),
            &raw_of(v.ty, &v.payload),
            &raw,
        );
        match packet::parse_packet(&raw) {
            Ok(Parsed::Known(parsed)) => assert_eq!(parsed, v.packet, "{}", v.name),
            other => panic!("{}: expected a Known packet, got {other:?}", v.name),
        }
    }
}

#[test]
fn wire_vectors_decode_parse_and_reencode_byte_identically() {
    let pixels = frame_pixels();
    for v in positive_vectors(&pixels) {
        let name = v.name;
        let wire = read_vector(&format!("{name}.wire"));
        let raw_file = read_vector(&format!("{name}.raw"));

        assert_eq!(wire.last(), Some(&0x00), "{name}: missing delimiter");
        let body = &wire[..wire.len() - 1];
        assert!(!body.contains(&0x00), "{name}: 0x00 inside the COBS body");

        let mut raw = vec![0u8; MAX_RAW_LEN];
        let raw_len = cobs::decode(body, &mut raw)
            .unwrap_or_else(|err| panic!("{name}: COBS decode failed: {err:?}"));
        assert_bytes_eq(
            &format!("{name}: decoded wire body"),
            &raw[..raw_len],
            &raw_file,
        );

        match packet::parse_packet(&raw[..raw_len]) {
            Ok(Parsed::Known(parsed)) => assert_eq!(parsed, v.packet, "{name}"),
            other => panic!("{name}: expected a Known packet, got {other:?}"),
        }
        assert_eq!(classify(name, &wire), Ok(()), "{name}: decode pipeline");

        let mut scratch = vec![0u8; MAX_RAW_LEN];
        let mut out = vec![0u8; MAX_WIRE_LEN];
        let wire_len = v
            .packet
            .encode(&mut scratch, &mut out)
            .unwrap_or_else(|err| panic!("{name}: typed encode failed: {err:?}"));
        assert_bytes_eq(
            &format!("{name}.wire typed re-encode"),
            &out[..wire_len],
            &wire,
        );
    }
}

#[test]
fn cobs_boundary_254_wire_is_the_canonical_listing_1_form() {
    let wire = read_vector("cobs_boundary_254.wire");
    let raw_file = read_vector("cobs_boundary_254.raw");
    let len = wire.len();

    // The committed raw file is exactly what the deterministic builder
    // produces; valid CRC + unassigned type parses as Unknown for
    // caller-side ignoring — never an error.
    assert_bytes_eq("cobs_boundary_254.raw builder", &boundary_raw(), &raw_file);
    match packet::parse_packet(&raw_file) {
        Ok(Parsed::Unknown { ty, payload }) => {
            assert_eq!(ty, BOUNDARY_TYPE_BYTE);
            assert_eq!(payload, &raw_file[1..raw_file.len() - 2]);
        }
        other => panic!("expected an Unknown packet, got {other:?}"),
    }

    assert_eq!(wire[len - 1], 0x00, "missing delimiter");
    assert_eq!(
        wire[len - 2],
        0x01,
        "canonical trailing group header missing (Wikipedia-variant encoder?)"
    );
    assert_eq!(
        wire[len - 257],
        0xFF,
        "expected a full 254-byte group before the trailing header"
    );

    let mut raw = vec![0u8; MAX_RAW_LEN];
    let n = cobs::decode(&wire[..len - 1], &mut raw).expect("COBS decode");
    assert_bytes_eq("cobs_boundary_254 decoded wire body", &raw[..n], &raw_file);

    // The non-canonical (Wikipedia) form — the same bytes minus the
    // trailing 0x01 header — decodes to the identical raw packet. That
    // round-trip blindness is exactly why only the byte-exact comparison
    // against this committed wire catches a Wikipedia-derived encoder port.
    let mut raw2 = vec![0u8; MAX_RAW_LEN];
    let m = cobs::decode(&wire[..len - 2], &mut raw2).expect("decode non-canonical form");
    assert_bytes_eq(
        "non-canonical form must decode identically",
        &raw2[..m],
        &raw_file,
    );
}

#[test]
fn negative_vectors_fail_with_exactly_the_expected_class() {
    for n in negative_vectors() {
        let wire = read_vector(&format!("{}.wire", n.name));
        assert_bytes_eq(&format!("{}.wire builder", n.name), &n.wire, &wire);
        assert_eq!(classify(n.name, &wire), Err(n.class), "{}", n.name);
    }
}

#[test]
fn resync_stream_recovers_exactly_the_embedded_packets() {
    let stream = read_vector("resync.stream");

    // Layout: garbage (delimiter-free), a lone 0x00, then the on-disk
    // hello.wire and brightness.wire verbatim.
    assert!(
        !RESYNC_GARBAGE.contains(&0x00),
        "garbage must not contain a delimiter"
    );
    let mut expected_stream = RESYNC_GARBAGE.to_vec();
    expected_stream.push(0x00);
    expected_stream.extend_from_slice(&read_vector("hello.wire"));
    expected_stream.extend_from_slice(&read_vector("brightness.wire"));
    assert_bytes_eq("resync.stream layout", &stream, &expected_stream);

    // Walk the stream the way a receiver does: split on 0x00, feed each
    // non-empty segment through COBS + parse, drop failures.
    let mut recovered: Vec<Vec<u8>> = Vec::new();
    for segment in stream.split(|&b| b == 0x00) {
        if segment.is_empty() {
            continue;
        }
        let mut raw = vec![0u8; MAX_RAW_LEN];
        let Ok(n) = cobs::decode(segment, &mut raw) else {
            continue;
        };
        if packet::parse_packet(&raw[..n]).is_ok() {
            recovered.push(raw[..n].to_vec());
        }
    }

    assert_eq!(recovered.len(), 2, "expected exactly two recovered packets");
    assert_eq!(
        packet::parse_packet(&recovered[0]),
        Ok(Parsed::Known(Packet::Hello {
            protocol_version: PROTOCOL_VERSION
        }))
    );
    assert_eq!(
        packet::parse_packet(&recovered[1]),
        Ok(Parsed::Known(Packet::Brightness {
            value: BRIGHTNESS_VALUE
        }))
    );
}

// Regeneration — deliberately #[ignore]d so it never runs as a test side
// effect. Only `just golden-regen` invokes it, via
// `cargo test -p proto --test golden_vectors -- --ignored regen`.

#[test]
#[ignore = "rewrites the golden vectors; run only via just golden-regen"]
fn regen() {
    let dir = testdata_dir();
    fs::create_dir_all(&dir).expect("create testdata/proto");
    for (name, bytes) in all_files() {
        fs::write(dir.join(&name), &bytes)
            .unwrap_or_else(|err| panic!("write testdata/proto/{name}: {err}"));
    }
}
