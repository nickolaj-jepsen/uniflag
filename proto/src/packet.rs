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
//! A *known* type with a wrong-length payload is also dropped
//! ([`Error::BadLength`]).
//!
//! Payload schemas are frozen as of M6: the typed [`Packet`] layer below
//! is the Rust source of truth, mirrored in prose by `docs/protocol.md`
//! and in bytes by the golden vectors under `testdata/proto/`. Any
//! wire-visible change bumps [`PROTOCOL_VERSION`].

use crate::{cobs, crc};

/// Protocol version carried in the [`Packet::Hello`] /
/// [`Packet::HelloAck`] handshake. The plugin requires exact equality and
/// refuses to drive the device on mismatch â€” bump on any wire-visible
/// change after the M6 freeze.
pub const PROTOCOL_VERSION: u8 = 1;

/// USB vendor id the device enumerates with and hosts filter on during
/// discovery: pid.codes' shared vendor id (see docs/protocol.md
/// §Transport).
pub const USB_VID: u16 = 0x1209;

/// USB product id. `0x0001` is the pid.codes **test PID**; the registered
/// PID `0xF1A6` replaces it once granted (docs/v2-tracking.md). Hosts
/// should accept both during the transition.
pub const USB_PID: u16 = 0x0001;

pub const PANEL_WIDTH: usize = 32;
pub const PANEL_HEIGHT: usize = 32;

/// `Frame` payload: RGB888, row-major from the top-left, 3 bytes per pixel.
pub const FRAME_PAYLOAD_LEN: usize = PANEL_WIDTH * PANEL_HEIGHT * 3;

/// Largest raw packet: type byte + Frame payload + CRC.
pub const MAX_RAW_LEN: usize = 1 + FRAME_PAYLOAD_LEN + 2;

/// Largest on-the-wire packet, including the trailing `0x00` delimiter.
/// Sizes the firmware RX accumulator.
pub const MAX_WIRE_LEN: usize = cobs::max_encoded_len(MAX_RAW_LEN) + 1;

/// Packet type bytes. Hostâ†’device types have the high bit clear,
/// deviceâ†’host types have it set.
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
    /// CRC mismatch â€” drop the packet and resync.
    BadCrc,
    /// Known packet type whose payload length doesn't match the frozen
    /// layout (or, on the encode side, an over-long `fw_version`).
    /// Receivers drop such packets â€” same posture as [`Error::BadCrc`].
    BadLength,
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
    // An undersized `wire` reports BufferTooSmall regardless of whether the
    // COBS layer or the delimiter byte ran out of room â€” one caller
    // mistake, one error value.
    let enc_len = cobs::encode(&scratch[..raw_len], wire).map_err(|e| match e {
        cobs::Error::BufferTooSmall => Error::BufferTooSmall,
        other => Error::Cobs(other),
    })?;
    if enc_len >= wire.len() {
        return Err(Error::BufferTooSmall);
    }
    wire[enc_len] = 0x00;
    Ok(enc_len + 1)
}

/// Validate a COBS-decoded raw packet and split it into (type byte,
/// payload). The type byte is returned raw so callers can ignore unknown
/// types explicitly (forward compat) rather than erroring here.
///
/// The payload length is **not** validated against the type â€” a valid-CRC
/// `Frame` with 5 bytes of payload parses fine. Callers must length-check
/// before use (e.g. a Frame payload must be exactly
/// [`FRAME_PAYLOAD_LEN`]); [`parse_packet`] / [`Packet::from_payload`]
/// are the typed layer that enforces this.
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

// ---------------------------------------------------------------------
// Typed layer (M6 freeze). One variant per assigned type; the payload
// layouts are immutable wire contracts â€” golden vectors under
// `testdata/proto/` and `docs/protocol.md` Â§"Payload layouts" pin the
// exact bytes. All multi-byte values are little-endian (today only the
// framing-layer CRC is multi-byte; every payload field is a single byte
// or a byte string).
// ---------------------------------------------------------------------

/// Fixed payload length of [`Packet::Hello`].
pub const HELLO_PAYLOAD_LEN: usize = 1;
/// Fixed payload length of [`Packet::Brightness`].
pub const BRIGHTNESS_PAYLOAD_LEN: usize = 1;
/// Fixed payload length of [`Packet::ButtonEvent`].
pub const BUTTON_EVENT_PAYLOAD_LEN: usize = 2;
/// Minimum payload length of [`Packet::HelloAck`] â€” the 3-byte header
/// without the variable firmware-version tail.
pub const HELLO_ACK_MIN_PAYLOAD_LEN: usize = 3;

/// Encoder-side cap on the `HelloAck` firmware-version string; sizes the
/// stack buffer inside [`Packet::encode`]. **Not** a wire limit â€” parsers
/// accept any length the framing allows (see
/// `hello_ack_parse_accepts_fw_longer_than_encoder_cap`).
pub const MAX_FW_VERSION_LEN: usize = 32;

/// Button ids carried in [`Packet::ButtonEvent`]. The id space is open â€”
/// unassigned bytes still parse (forward compat), so interpret via
/// [`Button::from_byte`] and ignore `None`.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Button {
    /// GPIO 21 â€” brightness up.
    BrightnessUp = 0,
    /// GPIO 26 â€” brightness down.
    BrightnessDown = 1,
    /// GPIO 27 â€” sleep.
    Sleep = 2,
}

impl Button {
    pub fn from_byte(byte: u8) -> Option<Self> {
        match byte {
            0 => Some(Self::BrightnessUp),
            1 => Some(Self::BrightnessDown),
            2 => Some(Self::Sleep),
            _ => None,
        }
    }

    pub fn to_byte(self) -> u8 {
        self as u8
    }
}

/// Press kinds carried in [`Packet::ButtonEvent`]. Classification happens
/// on release: a long press never *also* fires a short press.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum PressKind {
    /// Short press, classified on release.
    Short = 0,
    /// Long press.
    Long = 1,
}

impl PressKind {
    pub fn from_byte(byte: u8) -> Option<Self> {
        match byte {
            0 => Some(Self::Short),
            1 => Some(Self::Long),
            _ => None,
        }
    }

    pub fn to_byte(self) -> u8 {
        self as u8
    }
}

/// Typed, borrow-based view of one packet. Variants document their frozen
/// payload layout; wrong-length payloads never construct a `Packet`
/// ([`Error::BadLength`]).
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Packet<'a> {
    /// Hostâ†’device, `0x01`. Opens the handshake on every (re)connect;
    /// carries the host's [`PROTOCOL_VERSION`]. Exactly
    /// [`HELLO_PAYLOAD_LEN`] byte.
    Hello { protocol_version: u8 },
    /// Hostâ†’device, `0x02`. One full panel: RGB888, row-major from the
    /// top-left, 3 bytes per pixel (pixel `(x, y)` channel `c` at offset
    /// `(y*32 + x)*3 + c`, channels R, G, B). Exactly
    /// [`FRAME_PAYLOAD_LEN`] bytes.
    Frame { pixels: &'a [u8; FRAME_PAYLOAD_LEN] },
    /// Hostâ†’device, `0x03`. Display brightness multiplier `0..=255`,
    /// applied by the device pre-gamma to each channel as
    /// `(c * (value + 1)) >> 8`. Exactly [`BRIGHTNESS_PAYLOAD_LEN`] byte.
    Brightness { value: u8 },
    /// Deviceâ†’host, `0x81`. Handshake reply. At least
    /// [`HELLO_ACK_MIN_PAYLOAD_LEN`] bytes:
    /// `[protocol_version][width][height][fw_versionâ€¦]`.
    HelloAck {
        protocol_version: u8,
        /// Panel width in pixels (32 on the Cosmic Unicorn).
        width: u8,
        /// Panel height in pixels (32 on the Cosmic Unicorn).
        height: u8,
        /// Firmware version: ASCII, no NUL terminator, may be empty.
        fw_version: &'a [u8],
    },
    /// Deviceâ†’host, `0x82`. Exactly [`BUTTON_EVENT_PAYLOAD_LEN`] bytes.
    /// `button` / `kind` stay raw `u8`s so unassigned ids pass through
    /// parsing (forward compat) â€” interpret via [`Button`] /
    /// [`PressKind`].
    ButtonEvent { button: u8, kind: u8 },
}

/// Outcome of [`parse_packet`] on a CRC-valid raw packet. An unassigned
/// type byte is **not** an error â€” forward compat says receivers ignore
/// such packets â€” so it surfaces as [`Parsed::Unknown`] for the caller to
/// skip explicitly.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Parsed<'a> {
    Known(Packet<'a>),
    Unknown { ty: u8, payload: &'a [u8] },
}

impl<'a> Packet<'a> {
    /// The assigned type byte for this variant.
    pub fn packet_type(&self) -> PacketType {
        match self {
            Packet::Hello { .. } => PacketType::Hello,
            Packet::Frame { .. } => PacketType::Frame,
            Packet::Brightness { .. } => PacketType::Brightness,
            Packet::HelloAck { .. } => PacketType::HelloAck,
            Packet::ButtonEvent { .. } => PacketType::ButtonEvent,
        }
    }

    /// Typed view over the two halves [`parse_raw`] returns (known type +
    /// CRC-validated payload). [`Error::BadLength`] when the payload
    /// doesn't match the frozen layout â€” receivers drop the packet.
    pub fn from_payload(ty: PacketType, payload: &'a [u8]) -> Result<Self, Error> {
        match ty {
            PacketType::Hello => match payload {
                &[protocol_version] => Ok(Packet::Hello { protocol_version }),
                _ => Err(Error::BadLength),
            },
            PacketType::Frame => match payload.try_into() {
                Ok(pixels) => Ok(Packet::Frame { pixels }),
                Err(_) => Err(Error::BadLength),
            },
            PacketType::Brightness => match payload {
                &[value] => Ok(Packet::Brightness { value }),
                _ => Err(Error::BadLength),
            },
            PacketType::HelloAck => match payload {
                &[protocol_version, width, height, ref fw_version @ ..] => Ok(Packet::HelloAck {
                    protocol_version,
                    width,
                    height,
                    fw_version,
                }),
                _ => Err(Error::BadLength),
            },
            PacketType::ButtonEvent => match payload {
                &[button, kind] => Ok(Packet::ButtonEvent { button, kind }),
                _ => Err(Error::BadLength),
            },
        }
    }

    /// Full wire encode via [`encode`] (same buffer contract: `scratch`
    /// holds the raw form, `wire` the COBS form + delimiter; returns the
    /// wire length). The only typed-layer failure is a `HelloAck`
    /// `fw_version` longer than [`MAX_FW_VERSION_LEN`], which is
    /// [`Error::BadLength`].
    pub fn encode(&self, scratch: &mut [u8], wire: &mut [u8]) -> Result<usize, Error> {
        match *self {
            Packet::Hello { protocol_version } => {
                encode(PacketType::Hello, &[protocol_version], scratch, wire)
            }
            Packet::Frame { pixels } => encode(PacketType::Frame, pixels, scratch, wire),
            Packet::Brightness { value } => encode(PacketType::Brightness, &[value], scratch, wire),
            Packet::HelloAck {
                protocol_version,
                width,
                height,
                fw_version,
            } => {
                if fw_version.len() > MAX_FW_VERSION_LEN {
                    return Err(Error::BadLength);
                }
                let mut payload = [0u8; HELLO_ACK_MIN_PAYLOAD_LEN + MAX_FW_VERSION_LEN];
                payload[0] = protocol_version;
                payload[1] = width;
                payload[2] = height;
                let len = HELLO_ACK_MIN_PAYLOAD_LEN + fw_version.len();
                payload[HELLO_ACK_MIN_PAYLOAD_LEN..len].copy_from_slice(fw_version);
                encode(PacketType::HelloAck, &payload[..len], scratch, wire)
            }
            Packet::ButtonEvent { button, kind } => {
                encode(PacketType::ButtonEvent, &[button, kind], scratch, wire)
            }
        }
    }
}

/// Validate a raw packet (via [`parse_raw`]) and type it. Framing
/// problems (short, bad CRC) come back as `Err`; an unassigned type byte
/// is `Ok(`[`Parsed::Unknown`]`)` â€” ignore it, never treat it as an
/// error; a known type with a wrong-length payload is
/// [`Error::BadLength`] â€” drop it like a CRC failure.
pub fn parse_packet(raw: &[u8]) -> Result<Parsed<'_>, Error> {
    let (ty, payload) = parse_raw(raw)?;
    match PacketType::from_byte(ty) {
        Some(known) => Packet::from_payload(known, payload).map(Parsed::Known),
        None => Ok(Parsed::Unknown { ty, payload }),
    }
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
        assert_eq!(HELLO_PAYLOAD_LEN, 1);
        assert_eq!(BRIGHTNESS_PAYLOAD_LEN, 1);
        assert_eq!(BUTTON_EVENT_PAYLOAD_LEN, 2);
        assert_eq!(HELLO_ACK_MIN_PAYLOAD_LEN, 3);
        assert_eq!(PROTOCOL_VERSION, 1);
        assert_eq!(Button::BrightnessUp.to_byte(), 0);
        assert_eq!(Button::BrightnessDown.to_byte(), 1);
        assert_eq!(Button::Sleep.to_byte(), 2);
        assert_eq!(PressKind::Short.to_byte(), 0);
        assert_eq!(PressKind::Long.to_byte(), 1);
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
        // The raw layer doesn't length-check payloads (the typed layer
        // does - a real Hello carries exactly 1 byte), so an empty
        // payload is legal here.
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
    fn undersized_wire_buffer_is_one_error() {
        // Both exhaustion points (COBS layer vs delimiter byte) report the
        // same caller mistake with the same error value.
        let payload = [0x01u8; FRAME_PAYLOAD_LEN];
        let mut scratch = [0u8; MAX_RAW_LEN];
        let mut wire = [0u8; MAX_WIRE_LEN];
        assert!(encode(PacketType::Frame, &payload, &mut scratch, &mut wire).is_ok());
        for short in [MAX_WIRE_LEN - 1, MAX_WIRE_LEN - 2] {
            let res = encode(
                PacketType::Frame,
                &payload,
                &mut scratch,
                &mut wire[..short],
            );
            assert_eq!(res, Err(Error::BufferTooSmall), "wire len {short}");
        }
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
        // A valid-CRC packet with an unassigned type byte parses fine â€”
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

    // ------------------------------------------------------------------
    // Typed layer (M6 freeze)
    // ------------------------------------------------------------------

    const ALL_BUTTONS: [Button; 3] = [Button::BrightnessUp, Button::BrightnessDown, Button::Sleep];
    const ALL_PRESS_KINDS: [PressKind; 2] = [PressKind::Short, PressKind::Long];

    /// Push `pkt` through the full stack â€” typed encode â†’ COBS decode â†’
    /// typed parse â€” asserting the wire invariants on the way, and return
    /// the parsed-back packet (borrowing `raw`).
    fn full_stack<'a>(pkt: &Packet<'_>, raw: &'a mut [u8; MAX_RAW_LEN]) -> Packet<'a> {
        let mut scratch = [0u8; MAX_RAW_LEN];
        let mut wire = [0u8; MAX_WIRE_LEN];
        let wire_len = pkt.encode(&mut scratch, &mut wire).expect("encode");
        assert_eq!(wire[wire_len - 1], 0x00, "missing wire delimiter");
        assert!(!wire[..wire_len - 1].contains(&0), "0x00 inside COBS body");
        let raw_len = cobs::decode(&wire[..wire_len - 1], &mut raw[..]).expect("cobs decode");
        match parse_packet(&raw[..raw_len]).expect("parse") {
            Parsed::Known(parsed) => parsed,
            Parsed::Unknown { ty, .. } => panic!("parsed as unknown type {ty:#04x}"),
        }
    }

    /// Typed-encode `pkt`, COBS-decode, and return the raw *body* (type
    /// byte + payload, CRC stripped) for byte-level layout assertions.
    fn raw_body<'a>(pkt: &Packet<'_>, raw: &'a mut [u8; MAX_RAW_LEN]) -> &'a [u8] {
        let mut scratch = [0u8; MAX_RAW_LEN];
        let mut wire = [0u8; MAX_WIRE_LEN];
        let wire_len = pkt.encode(&mut scratch, &mut wire).expect("encode");
        let raw_len = cobs::decode(&wire[..wire_len - 1], &mut raw[..]).expect("cobs decode");
        &raw[..raw_len - 2]
    }

    #[test]
    fn typed_hello_round_trips_boundary_versions() {
        for protocol_version in [0u8, 1, 0x7F, 0xFF] {
            let pkt = Packet::Hello { protocol_version };
            let mut raw = [0u8; MAX_RAW_LEN];
            assert_eq!(
                full_stack(&pkt, &mut raw),
                pkt,
                "version {protocol_version}"
            );
        }
    }

    #[test]
    fn typed_frame_round_trips() {
        // Pattern includes 0x00 bytes so the COBS leg does real work.
        let pixels: [u8; FRAME_PAYLOAD_LEN] = core::array::from_fn(|i| (i % 256) as u8);
        let pkt = Packet::Frame { pixels: &pixels };
        let mut raw = [0u8; MAX_RAW_LEN];
        let parsed = full_stack(&pkt, &mut raw);
        assert_eq!(parsed, pkt);
        assert_eq!(parsed.packet_type(), PacketType::Frame);
    }

    #[test]
    fn typed_brightness_round_trips_boundary_values() {
        for value in [0u8, 1, 0x80, 0xFE, 0xFF] {
            let pkt = Packet::Brightness { value };
            let mut raw = [0u8; MAX_RAW_LEN];
            assert_eq!(full_stack(&pkt, &mut raw), pkt, "value {value}");
        }
    }

    #[test]
    fn typed_hello_ack_round_trips_empty_and_max_fw_version() {
        let max_fw = [b'x'; MAX_FW_VERSION_LEN];
        let cases: [&[u8]; 3] = [b"", b"0.2.0", &max_fw];
        for fw_version in cases {
            let pkt = Packet::HelloAck {
                protocol_version: PROTOCOL_VERSION,
                width: 32,
                height: 32,
                fw_version,
            };
            let mut raw = [0u8; MAX_RAW_LEN];
            assert_eq!(
                full_stack(&pkt, &mut raw),
                pkt,
                "fw len {}",
                fw_version.len()
            );
        }
    }

    #[test]
    fn typed_button_event_round_trips_every_assigned_combo() {
        for button in ALL_BUTTONS {
            for kind in ALL_PRESS_KINDS {
                let pkt = Packet::ButtonEvent {
                    button: button.to_byte(),
                    kind: kind.to_byte(),
                };
                let mut raw = [0u8; MAX_RAW_LEN];
                assert_eq!(full_stack(&pkt, &mut raw), pkt, "{button:?}/{kind:?}");
            }
        }
    }

    #[test]
    fn packet_type_matches_variant() {
        let pixels = [0u8; FRAME_PAYLOAD_LEN];
        let cases: [(Packet, PacketType); 5] = [
            (
                Packet::Hello {
                    protocol_version: 1,
                },
                PacketType::Hello,
            ),
            (Packet::Frame { pixels: &pixels }, PacketType::Frame),
            (Packet::Brightness { value: 1 }, PacketType::Brightness),
            (
                Packet::HelloAck {
                    protocol_version: 1,
                    width: 32,
                    height: 32,
                    fw_version: b"",
                },
                PacketType::HelloAck,
            ),
            (
                Packet::ButtonEvent { button: 0, kind: 0 },
                PacketType::ButtonEvent,
            ),
        ];
        for (pkt, ty) in cases {
            assert_eq!(pkt.packet_type(), ty);
        }
    }

    #[test]
    fn payload_layouts_are_frozen() {
        // Byte-exact golden layouts (M6 freeze; mirrors docs/protocol.md
        // Â§"Payload layouts"). Changing any assertion here is a
        // wire-protocol break.
        let mut raw = [0u8; MAX_RAW_LEN];
        assert_eq!(
            raw_body(
                &Packet::Hello {
                    protocol_version: 0x2A
                },
                &mut raw
            ),
            [0x01, 0x2A]
        );
        assert_eq!(
            raw_body(&Packet::Brightness { value: 0x80 }, &mut raw),
            [0x03, 0x80]
        );
        // Byte order is button, then kind.
        assert_eq!(
            raw_body(
                &Packet::ButtonEvent {
                    button: 0x02,
                    kind: 0x01
                },
                &mut raw
            ),
            [0x82, 0x02, 0x01]
        );
        // Byte order is protocol_version, width, height, fw_version.
        assert_eq!(
            raw_body(
                &Packet::HelloAck {
                    protocol_version: 0x01,
                    width: 32,
                    height: 32,
                    fw_version: b"0.1",
                },
                &mut raw,
            ),
            [0x81, 0x01, 0x20, 0x20, b'0', b'.', b'1']
        );
        // Frame: type byte, then the 3072 payload bytes verbatim â€”
        // row-major RGB, pixel (x, y) channel c at body[1 + (y*32+x)*3 + c].
        let mut pixels = [0u8; FRAME_PAYLOAD_LEN];
        let (x, y) = (5usize, 7usize);
        pixels[(y * PANEL_WIDTH + x) * 3] = 0xAA; // R
        pixels[(y * PANEL_WIDTH + x) * 3 + 2] = 0xBB; // B
        let body = raw_body(&Packet::Frame { pixels: &pixels }, &mut raw);
        assert_eq!(body.len(), 1 + FRAME_PAYLOAD_LEN);
        assert_eq!(body[0], 0x02);
        assert_eq!(body[1 + (y * PANEL_WIDTH + x) * 3], 0xAA);
        assert_eq!(body[1 + (y * PANEL_WIDTH + x) * 3 + 2], 0xBB);
        assert_eq!(&body[1..], pixels);
    }

    #[test]
    fn wrong_length_known_packets_are_rejected() {
        // A wrong-length payload for a *known* type is BadLength â€” the
        // receiver drops it like a CRC failure. One under and one over
        // per fixed-size type; every below-minimum length for HelloAck.
        let cases: &[(PacketType, usize)] = &[
            (PacketType::Hello, 0),
            (PacketType::Hello, 2),
            (PacketType::Frame, 0),
            (PacketType::Frame, FRAME_PAYLOAD_LEN - 1),
            (PacketType::Frame, FRAME_PAYLOAD_LEN + 1),
            (PacketType::Brightness, 0),
            (PacketType::Brightness, 2),
            (PacketType::HelloAck, 0),
            (PacketType::HelloAck, 1),
            (PacketType::HelloAck, 2),
            (PacketType::ButtonEvent, 0),
            (PacketType::ButtonEvent, 1),
            (PacketType::ButtonEvent, 3),
        ];
        let payload = [0u8; FRAME_PAYLOAD_LEN + 1];
        let mut raw = [0u8; MAX_RAW_LEN + 1];
        for &(ty, len) in cases {
            let raw_len = write_raw(ty, &payload[..len], &mut raw).expect("write_raw");
            assert_eq!(
                parse_packet(&raw[..raw_len]),
                Err(Error::BadLength),
                "{ty:?} with {len}-byte payload"
            );
        }
    }

    #[test]
    fn unknown_type_surfaces_as_unknown_not_error() {
        // Forward compat: a valid-CRC packet with an unassigned type byte
        // is Parsed::Unknown, never an error â€” receivers skip it.
        let mut raw = [0u8; 8];
        raw[0] = 0x7E;
        raw[1] = 0x42;
        let checksum = crc::checksum(&raw[..2]);
        raw[2..4].copy_from_slice(&checksum.to_le_bytes());
        assert_eq!(
            parse_packet(&raw[..4]),
            Ok(Parsed::Unknown {
                ty: 0x7E,
                payload: &[0x42],
            })
        );
    }

    #[test]
    fn typed_parse_propagates_framing_errors() {
        assert_eq!(parse_packet(&[0x01, 0x00]), Err(Error::TooShort));
        let mut raw = [0u8; 8];
        let raw_len = write_raw(PacketType::Hello, &[1], &mut raw).expect("write_raw");
        raw[1] ^= 0x01;
        assert_eq!(parse_packet(&raw[..raw_len]), Err(Error::BadCrc));
    }

    #[test]
    fn hello_ack_fw_version_over_encoder_cap_is_rejected() {
        let fw = [b'x'; MAX_FW_VERSION_LEN + 1];
        let pkt = Packet::HelloAck {
            protocol_version: 1,
            width: 32,
            height: 32,
            fw_version: &fw,
        };
        let mut scratch = [0u8; MAX_RAW_LEN];
        let mut wire = [0u8; MAX_WIRE_LEN];
        assert_eq!(pkt.encode(&mut scratch, &mut wire), Err(Error::BadLength));
    }

    #[test]
    fn hello_ack_parse_accepts_fw_longer_than_encoder_cap() {
        // MAX_FW_VERSION_LEN is encoder-side only; the wire layout has no
        // fw_version length limit.
        let mut payload = [b'y'; HELLO_ACK_MIN_PAYLOAD_LEN + MAX_FW_VERSION_LEN + 5];
        payload[0] = PROTOCOL_VERSION;
        payload[1] = 32;
        payload[2] = 32;
        let mut raw = [0u8; 64];
        let raw_len = write_raw(PacketType::HelloAck, &payload, &mut raw).expect("write_raw");
        match parse_packet(&raw[..raw_len]).expect("parse") {
            Parsed::Known(Packet::HelloAck { fw_version, .. }) => {
                assert_eq!(fw_version.len(), MAX_FW_VERSION_LEN + 5);
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    #[test]
    fn button_event_with_unassigned_ids_still_parses() {
        // The 2-byte length is frozen but the id space is open â€” a future
        // firmware button must not kill old parsers.
        let mut raw = [0u8; 8];
        let raw_len = write_raw(PacketType::ButtonEvent, &[7, 9], &mut raw).expect("write_raw");
        assert_eq!(
            parse_packet(&raw[..raw_len]),
            Ok(Parsed::Known(Packet::ButtonEvent { button: 7, kind: 9 }))
        );
        assert_eq!(Button::from_byte(7), None);
        assert_eq!(PressKind::from_byte(9), None);
    }

    #[test]
    fn button_and_press_kind_bytes_round_trip() {
        for button in ALL_BUTTONS {
            assert_eq!(Button::from_byte(button.to_byte()), Some(button));
        }
        for kind in ALL_PRESS_KINDS {
            assert_eq!(PressKind::from_byte(kind.to_byte()), Some(kind));
        }
        assert_eq!(Button::from_byte(3), None);
        assert_eq!(PressKind::from_byte(2), None);
    }

    #[test]
    fn typed_encode_matches_low_level_encode() {
        // Packet::encode is a thin wrapper over packet::encode â€” the wire
        // bytes must be identical.
        let pkt = Packet::HelloAck {
            protocol_version: 1,
            width: 32,
            height: 32,
            fw_version: b"1.0",
        };
        let mut scratch_a = [0u8; MAX_RAW_LEN];
        let mut wire_a = [0u8; MAX_WIRE_LEN];
        let len_a = pkt
            .encode(&mut scratch_a, &mut wire_a)
            .expect("typed encode");
        let mut scratch_b = [0u8; MAX_RAW_LEN];
        let mut wire_b = [0u8; MAX_WIRE_LEN];
        let len_b = encode(
            PacketType::HelloAck,
            &[1, 32, 32, b'1', b'.', b'0'],
            &mut scratch_b,
            &mut wire_b,
        )
        .expect("low-level encode");
        assert_eq!(&wire_a[..len_a], &wire_b[..len_b]);
    }
}
