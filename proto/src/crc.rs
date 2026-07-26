//! CRC-16 for the v2 binary protocol.
//!
//! Parameters — **CRC-16/CCITT-FALSE**:
//!
//! | parameter | value    |
//! |-----------|----------|
//! | poly      | `0x1021` |
//! | init      | `0xFFFF` |
//! | refin     | false    |
//! | refout    | false    |
//! | xorout    | `0x0000` |
//! | check     | `crc("123456789") == 0x29B1` |
//!
//! Chosen because it is the most widely cross-checked 16-bit CRC (easy to
//! validate a C# port against third-party implementations), the table
//! variant costs 512 B and ~1 lookup/byte (fine for 3 KB frames at 30 fps
//! on the RP2040), and the RP2040 DMA sniffer implements the same
//! polynomial in hardware should the firmware ever want it.
//!
//! The CRC is computed over the *raw* packet bytes (type byte + payload),
//! before COBS encoding. See [`crate::packet`] for the framing layout.

const POLY: u16 = 0x1021;

/// Initial CRC register value. Feed to [`update`] when streaming.
pub const INIT: u16 = 0xFFFF;

static TABLE: [u16; 256] = build_table();

const fn build_table() -> [u16; 256] {
    let mut table = [0u16; 256];
    let mut i = 0;
    while i < 256 {
        let mut crc = (i as u16) << 8;
        let mut bit = 0;
        while bit < 8 {
            crc = if crc & 0x8000 != 0 {
                (crc << 1) ^ POLY
            } else {
                crc << 1
            };
            bit += 1;
        }
        table[i] = crc;
        i += 1;
    }
    table
}

/// Streaming update: fold `data` into a running CRC started from [`INIT`].
#[must_use]
pub fn update(mut crc: u16, data: &[u8]) -> u16 {
    for &byte in data {
        let idx = ((crc >> 8) ^ byte as u16) & 0xFF;
        crc = (crc << 8) ^ TABLE[idx as usize];
    }
    crc
}

/// One-shot CRC of `data`.
#[must_use]
pub fn checksum(data: &[u8]) -> u16 {
    update(INIT, data)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn check_value_matches_crc16_ccitt_false() {
        // The canonical "check" value from the CRC catalogue
        // (reveng / crccalc): CRC-16/CCITT-FALSE of "123456789".
        assert_eq!(checksum(b"123456789"), 0x29B1);
    }

    #[test]
    fn empty_input_is_init() {
        assert_eq!(checksum(&[]), INIT);
    }

    #[test]
    fn streaming_equals_one_shot() {
        let data = b"the quick brown fox jumps over the lazy dog";
        for split in 0..=data.len() {
            let (a, b) = data.split_at(split);
            assert_eq!(update(update(INIT, a), b), checksum(data));
        }
    }

    #[test]
    fn detects_single_bit_flips() {
        let mut data = [0u8; 64];
        for (i, byte) in data.iter_mut().enumerate() {
            *byte = i as u8;
        }
        let good = checksum(&data);
        for i in 0..data.len() {
            for bit in 0..8 {
                data[i] ^= 1 << bit;
                assert_ne!(checksum(&data), good, "flip at byte {i} bit {bit}");
                data[i] ^= 1 << bit;
            }
        }
    }
}
