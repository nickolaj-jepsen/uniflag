//! COBS (Consistent Overhead Byte Stuffing) framing, `no_std`, no alloc.
//!
//! Encoded output never contains `0x00`; packets are delimited on the wire
//! by a single `0x00` **which these functions neither produce nor consume**
//! — the transport layer appends it after [`encode`] and strips it before
//! [`decode`]. That property is what makes stream resync trivial: after
//! any corruption, skip to the next `0x00` and the decoder is realigned.
//!
//! Canonical form: this encoder always terminates with a group header —
//! Cheshire & Baker's Listing 1 (`StuffData`), the same convention as
//! Craig McQueen's cobs-c and the jamesmunns `cobs` crate — so a payload
//! that is an exact multiple of 254 non-zero bytes ends with a trailing
//! `0x01` code byte. **Beware: Wikipedia's `cobsEncode` example omits that
//! trailing byte**, and the divergence is silent under round-trip testing
//! because the decoder (like every decoder) accepts both forms. The
//! cross-language golden vectors pin the Listing-1 choice and include a
//! 254-boundary case precisely so a Wikipedia-derived port fails loudly.

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Error {
    /// Destination buffer too small for the result.
    BufferTooSmall,
    /// Encoded input malformed: embedded `0x00`, a group running past the
    /// end of the input, or an empty input (the empty payload encodes to
    /// `[0x01]`, never to nothing).
    Malformed,
}

/// Worst-case encoded size for a `payload_len`-byte payload (excluding the
/// wire delimiter): one code byte per started 254-byte group, plus the
/// always-emitted final group header (an extra byte when the payload is an
/// exact non-zero multiple of 254). Exactly tight for zero-free payloads.
pub const fn max_encoded_len(payload_len: usize) -> usize {
    payload_len + payload_len / 254 + 1
}

/// Encode `src` into `dst`; returns the encoded length.
pub fn encode(src: &[u8], dst: &mut [u8]) -> Result<usize, Error> {
    if dst.is_empty() {
        return Err(Error::BufferTooSmall);
    }
    // `code_idx` is the reserved slot for the current group's code byte;
    // `out` is the next free slot.
    let mut code_idx = 0usize;
    let mut out = 1usize;
    let mut code = 1u8;

    for &byte in src {
        if byte == 0 {
            dst[code_idx] = code;
            code_idx = out;
            if code_idx >= dst.len() {
                return Err(Error::BufferTooSmall);
            }
            out += 1;
            code = 1;
        } else {
            if out >= dst.len() {
                return Err(Error::BufferTooSmall);
            }
            dst[out] = byte;
            out += 1;
            code += 1;
            if code == 0xFF {
                dst[code_idx] = code;
                code_idx = out;
                if code_idx >= dst.len() {
                    return Err(Error::BufferTooSmall);
                }
                out += 1;
                code = 1;
            }
        }
    }
    dst[code_idx] = code;
    Ok(out)
}

/// Decode `src` (one delimiter-stripped encoded packet) into `dst`;
/// returns the decoded length.
pub fn decode(src: &[u8], dst: &mut [u8]) -> Result<usize, Error> {
    if src.is_empty() {
        return Err(Error::Malformed);
    }
    let mut out = 0usize;
    let mut i = 0usize;
    while i < src.len() {
        let code = src[i];
        if code == 0 {
            return Err(Error::Malformed);
        }
        i += 1;
        let run = code as usize - 1;
        if i + run > src.len() {
            return Err(Error::Malformed);
        }
        if out + run > dst.len() {
            return Err(Error::BufferTooSmall);
        }
        // A valid encoding never contains 0x00 anywhere — including group
        // data. Unreachable from a delimiter-splitting transport, but the
        // strictness keeps decoder behaviour fully defined for the
        // cross-language conformance vectors.
        if src[i..i + run].contains(&0) {
            return Err(Error::Malformed);
        }
        dst[out..out + run].copy_from_slice(&src[i..i + run]);
        out += run;
        i += run;
        // Every group except a full (0xFF) one and the final one stands in
        // for a zero byte of the payload.
        if code != 0xFF && i < src.len() {
            if out >= dst.len() {
                return Err(Error::BufferTooSmall);
            }
            dst[out] = 0;
            out += 1;
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn round_trip(payload: &[u8]) {
        let mut enc = [0u8; 4096];
        let mut dec = [0u8; 4096];
        let n = encode(payload, &mut enc).expect("encode");
        assert!(
            n <= max_encoded_len(payload.len()),
            "bound violated for len {}",
            payload.len()
        );
        assert!(!enc[..n].contains(&0), "encoded output contains 0x00");
        let m = decode(&enc[..n], &mut dec).expect("decode");
        assert_eq!(
            &dec[..m],
            payload,
            "round trip failed for len {}",
            payload.len()
        );
    }

    #[test]
    fn empty_payload() {
        let mut enc = [0u8; 4];
        let n = encode(&[], &mut enc).expect("encode");
        assert_eq!(&enc[..n], &[0x01]);
        round_trip(&[]);
    }

    #[test]
    fn known_vectors() {
        // From the COBS paper / Wikipedia examples.
        let cases: &[(&[u8], &[u8])] = &[
            (&[0x00], &[0x01, 0x01]),
            (&[0x00, 0x00], &[0x01, 0x01, 0x01]),
            (&[0x11, 0x22, 0x00, 0x33], &[0x03, 0x11, 0x22, 0x02, 0x33]),
            (&[0x11, 0x22, 0x33, 0x44], &[0x05, 0x11, 0x22, 0x33, 0x44]),
            (&[0x11, 0x00, 0x00, 0x00], &[0x02, 0x11, 0x01, 0x01, 0x01]),
        ];
        for (payload, expected) in cases {
            let mut enc = [0u8; 16];
            let n = encode(payload, &mut enc).expect("encode");
            assert_eq!(&enc[..n], *expected);
            round_trip(payload);
        }
    }

    #[test]
    fn all_zero_payloads() {
        for len in 1..=520 {
            let payload = [0u8; 520];
            round_trip(&payload[..len]);
        }
        // Shape check: n zeros -> n+1 bytes of 0x01.
        let mut enc = [0u8; 8];
        let n = encode(&[0, 0, 0], &mut enc).expect("encode");
        assert_eq!(&enc[..n], &[1, 1, 1, 1]);
    }

    #[test]
    fn full_group_runs() {
        // Exactly 254 non-zero bytes: full group then the canonical
        // trailing empty group header.
        let payload: [u8; 254] = core::array::from_fn(|i| (i % 255) as u8 + 1);
        let mut enc = [0u8; 260];
        let n = encode(&payload, &mut enc).expect("encode");
        assert_eq!(n, 256);
        assert_eq!(enc[0], 0xFF);
        assert_eq!(enc[255], 0x01);
        round_trip(&payload);

        // The non-canonical form without the trailing header must decode
        // identically (decoder is liberal).
        let mut dec = [0u8; 260];
        let m = decode(&enc[..255], &mut dec).expect("decode");
        assert_eq!(&dec[..m], &payload);

        // 255 non-zero bytes: full group + 1.
        let payload: [u8; 255] = core::array::from_fn(|i| (i % 255) as u8 + 1);
        let mut enc = [0u8; 260];
        let n = encode(&payload, &mut enc).expect("encode");
        assert_eq!(n, 257);
        assert_eq!(enc[0], 0xFF);
        assert_eq!(enc[255], 0x02);
        round_trip(&payload);
    }

    #[test]
    fn round_trips_every_length_with_mixed_content() {
        // Patterned data with zeros sprinkled at varying strides, lengths
        // crossing both group boundaries (254, 508).
        let mut payload = [0u8; 600];
        for len in 0..=600 {
            for stride in [1usize, 3, 7, 254, 255] {
                for (i, byte) in payload[..len].iter_mut().enumerate() {
                    *byte = if i % stride == 0 {
                        0
                    } else {
                        (i % 255) as u8 + 1
                    };
                }
                round_trip(&payload[..len]);
            }
        }
    }

    #[test]
    fn frame_sized_round_trip() {
        // The largest packet the protocol carries: type + 3072 RGB bytes + CRC.
        let payload: [u8; 3075] = core::array::from_fn(|i| (i % 256) as u8);
        round_trip(&payload);
    }

    #[test]
    fn decode_rejects_malformed() {
        let mut dst = [0u8; 16];
        // Empty input is not a valid encoding.
        assert_eq!(decode(&[], &mut dst), Err(Error::Malformed));
        // Embedded zero.
        assert_eq!(decode(&[0x02, 0x00], &mut dst), Err(Error::Malformed));
        // Truncated: code byte promises more data than present.
        assert_eq!(decode(&[0x05, 0x11, 0x22], &mut dst), Err(Error::Malformed));
        assert_eq!(decode(&[0xFF, 0x11], &mut dst), Err(Error::Malformed));
    }

    #[test]
    fn encode_reports_small_buffer() {
        let payload = [0x11u8; 32];
        let mut enc = [0u8; 8];
        assert_eq!(encode(&payload, &mut enc), Err(Error::BufferTooSmall));
        let mut enc = [0u8; 0];
        assert_eq!(encode(&[], &mut enc), Err(Error::BufferTooSmall));
    }

    #[test]
    fn decode_reports_small_buffer() {
        let payload = [0x11u8; 32];
        let mut enc = [0u8; 64];
        let n = encode(&payload, &mut enc).expect("encode");
        let mut small = [0u8; 8];
        assert_eq!(decode(&enc[..n], &mut small), Err(Error::BufferTooSmall));
    }
}
