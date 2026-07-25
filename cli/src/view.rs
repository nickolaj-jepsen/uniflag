//! Looking at a frame.
//!
//! The panel's whole job is to show a picture, so a diagnostic tool that
//! can only report byte counts is half a tool. This module turns a raw
//! RGB888 frame — one captured off the wire, or one emitted by a test
//! pattern — into something a person can actually look at: ANSI
//! half-blocks for the terminal, or a PNG.
//!
//! The PNG writer is hand-rolled and dependency-free. It emits *stored*
//! (uncompressed) deflate blocks, which zlib permits: a 32×32 frame is
//! kilobytes either way, and the alternative is pulling an image crate and
//! a compression crate into a tool whose whole point is to have few moving
//! parts. See RFC 1950/1951 and the PNG spec.

use proto::packet::{PANEL_HEIGHT, PANEL_WIDTH};

/// Bytes in one full frame.
pub const FRAME_LEN: usize = PANEL_WIDTH * PANEL_HEIGHT * 3;

const PNG_SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];
/// Largest payload a single stored deflate block can carry.
const MAX_STORED_BLOCK: usize = 65_535;

/// Render a frame as ANSI truecolour half-blocks: one character per two
/// pixel rows, so a 32×32 frame occupies 16 terminal lines at true aspect
/// ratio.
pub fn ansi(rgb: &[u8], width: usize, height: usize) -> String {
    let mut out = String::new();
    let at = |x: usize, y: usize| {
        let i = (y * width + x) * 3;
        (rgb[i], rgb[i + 1], rgb[i + 2])
    };

    let mut y = 0;
    while y < height {
        for x in 0..width {
            let (tr, tg, tb) = at(x, y);
            let (br, bg, bb) = if y + 1 < height {
                at(x, y + 1)
            } else {
                (0, 0, 0)
            };
            // \x1b[38;2;R;G;Bm = fg truecolour, \x1b[48;… = bg.
            out.push_str(&format!(
                "\x1b[38;2;{tr};{tg};{tb}m\x1b[48;2;{br};{bg};{bb}m\u{2580}"
            ));
        }
        out.push_str("\x1b[0m\n");
        y += 2;
    }
    out
}

/// Encode a frame as a truecolour PNG, nearest-neighbour upscaled by
/// `scale`. Nearest-neighbour on purpose: smooth scaling would invent
/// colours the renderer never produced, which is exactly the kind of lie
/// a diagnostic image must not tell.
pub fn png(rgb: &[u8], width: usize, height: usize, scale: usize) -> Vec<u8> {
    let scale = scale.max(1);
    let (out_w, out_h) = (width * scale, height * scale);

    // PNG image data: every scanline prefixed with its filter-type byte.
    let mut raw = Vec::with_capacity(out_h * (1 + out_w * 3));
    for y in 0..out_h {
        raw.push(0); // filter: none
        let src_row = (y / scale) * width * 3;
        for x in 0..out_w {
            let i = src_row + (x / scale) * 3;
            raw.extend_from_slice(&rgb[i..i + 3]);
        }
    }

    let mut png = Vec::from(PNG_SIGNATURE);

    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&(out_w as u32).to_be_bytes());
    ihdr.extend_from_slice(&(out_h as u32).to_be_bytes());
    ihdr.extend_from_slice(&[8, 2, 0, 0, 0]); // 8-bit, truecolour, deflate, adaptive, no interlace
    write_chunk(&mut png, b"IHDR", &ihdr);
    write_chunk(&mut png, b"IDAT", &zlib_stored(&raw));
    write_chunk(&mut png, b"IEND", &[]);
    png
}

/// Wrap `data` in a zlib stream built entirely from stored blocks.
fn zlib_stored(data: &[u8]) -> Vec<u8> {
    // CMF 0x78: deflate, 32 KiB window. FLG 0x01 makes CMF*256+FLG a
    // multiple of 31, as the format requires.
    let mut out = vec![0x78, 0x01];

    if data.is_empty() {
        out.extend_from_slice(&[0x01, 0x00, 0x00, 0xFF, 0xFF]);
    } else {
        for (i, chunk) in data.chunks(MAX_STORED_BLOCK).enumerate() {
            let last = (i + 1) * MAX_STORED_BLOCK >= data.len();
            out.push(if last { 1 } else { 0 }); // BFINAL, BTYPE=00 (stored)
            let len = chunk.len() as u16;
            out.extend_from_slice(&len.to_le_bytes());
            out.extend_from_slice(&(!len).to_le_bytes());
            out.extend_from_slice(chunk);
        }
    }

    out.extend_from_slice(&adler32(data).to_be_bytes());
    out
}

fn adler32(data: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for &byte in data {
        a = (a + byte as u32) % 65_521;
        b = (b + a) % 65_521;
    }
    (b << 16) | a
}

fn write_chunk(into: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    into.extend_from_slice(&(data.len() as u32).to_be_bytes());
    let start = into.len();
    into.extend_from_slice(kind);
    into.extend_from_slice(data);
    let crc = crc32(&into[start..]);
    into.extend_from_slice(&crc.to_be_bytes());
}

fn crc32(data: &[u8]) -> u32 {
    let mut c = 0xFFFF_FFFFu32;
    for &byte in data {
        c ^= byte as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 {
                0xEDB8_8320 ^ (c >> 1)
            } else {
                c >> 1
            };
        }
    }
    c ^ 0xFFFF_FFFF
}

#[cfg(test)]
mod tests {
    use super::*;

    fn gradient() -> Vec<u8> {
        (0..FRAME_LEN).map(|i| (i % 256) as u8).collect()
    }

    /// Walk the chunk structure the way a decoder would, validating every
    /// CRC. Catches a malformed length, a mistyped chunk, or a CRC computed
    /// over the wrong span.
    fn parse_chunks(png: &[u8]) -> Vec<(String, usize)> {
        assert_eq!(&png[..8], &PNG_SIGNATURE, "bad signature");
        let mut chunks = Vec::new();
        let mut i = 8;
        while i < png.len() {
            let len = u32::from_be_bytes([png[i], png[i + 1], png[i + 2], png[i + 3]]) as usize;
            let kind = String::from_utf8(png[i + 4..i + 8].to_vec()).expect("chunk type is ascii");
            let body = &png[i + 4..i + 8 + len];
            let want = u32::from_be_bytes([
                png[i + 8 + len],
                png[i + 9 + len],
                png[i + 10 + len],
                png[i + 11 + len],
            ]);
            assert_eq!(crc32(body), want, "{kind} chunk CRC mismatch");
            chunks.push((kind, len));
            i += 12 + len;
        }
        chunks
    }

    #[test]
    fn png_structure_is_wellformed() {
        let png = png(&gradient(), PANEL_WIDTH, PANEL_HEIGHT, 1);
        let chunks = parse_chunks(&png);
        let kinds: Vec<&str> = chunks.iter().map(|(k, _)| k.as_str()).collect();
        assert_eq!(kinds, vec!["IHDR", "IDAT", "IEND"]);
    }

    #[test]
    fn png_header_records_the_scaled_dimensions() {
        let png = png(&gradient(), PANEL_WIDTH, PANEL_HEIGHT, 4);
        let w = u32::from_be_bytes([png[16], png[17], png[18], png[19]]);
        let h = u32::from_be_bytes([png[20], png[21], png[22], png[23]]);
        assert_eq!((w, h), (128, 128));
        assert_eq!(png[24], 8, "bit depth");
        assert_eq!(png[25], 2, "colour type: truecolour");
    }

    /// A frame big enough to need several stored blocks — the boundary
    /// where a wrong BFINAL flag or LEN/NLEN pair would corrupt the stream.
    #[test]
    fn png_spans_multiple_stored_blocks() {
        let png = png(&gradient(), PANEL_WIDTH, PANEL_HEIGHT, 8);
        let idat = parse_chunks(&png)
            .into_iter()
            .find(|(k, _)| k == "IDAT")
            .expect("IDAT present");
        // 256 rows x (1 + 768) bytes = 196_864 > 3 x 65_535.
        assert!(
            idat.1 > 3 * MAX_STORED_BLOCK,
            "expected a multi-block stream"
        );
    }

    #[test]
    fn adler32_matches_the_known_vector() {
        // RFC 1950's worked example.
        assert_eq!(adler32(b"Wikipedia"), 0x11E6_0398);
    }

    #[test]
    fn ansi_uses_one_line_per_two_rows() {
        let text = ansi(&gradient(), PANEL_WIDTH, PANEL_HEIGHT);
        assert_eq!(text.lines().count(), PANEL_HEIGHT / 2);
    }
}
