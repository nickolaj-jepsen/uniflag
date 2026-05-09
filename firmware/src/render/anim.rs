//! Integer-only animation primitives. Cortex-M0+ has no FPU so everything
//! here is pure integer arithmetic with a lookup table for the sine.

/// 8-bit unsigned sine indexed by a phase 0..=255 (one full period).
/// Centred at 128: `SIN_U8[0] == 128`, peak ≈ 255 (at 64), trough ≈ 1
/// (at 192). Computed at compile time via Bhaskara I (≈0.16% error —
/// well under the panel's perceptible threshold).
pub static SIN_U8: [u8; 256] = build_sin_lut();

const fn build_sin_lut() -> [u8; 256] {
    let mut out = [0u8; 256];
    let mut k: u32 = 0;
    while k < 256 {
        let neg = k >= 128;
        let a = k % 128;
        let q = a * (128 - a);
        let mag = (16 * q * 127) / (81920 - 4 * q);
        out[k as usize] = if neg {
            128u8.saturating_sub(mag as u8)
        } else {
            (128 + mag) as u8
        };
        k += 1;
    }
    out
}

/// 60 fps strobe with 60 % duty. Returns `true` during the on-phase.
/// `hz` ≥ 1; behaviour pinned to `FRAME_TICK_MS = 16`.
pub fn strobe_60(frame: u32, hz: u8) -> bool {
    let period = (60 / hz as u32).max(1);
    let on = (period * 6 + 5) / 10;
    (frame % period) < on
}

/// Half-sine envelope 0..=255 over `period_frames` frames. Useful as a
/// breathing modulator on a base colour.
pub fn breathe(frame: u32, period_frames: u32) -> u8 {
    let p = (frame % period_frames) * 256 / period_frames;
    SIN_U8[(p & 0xFF) as usize]
}

/// Per-pixel diagonal cloth-wave brightness modulator. Returns a 0..=255
/// multiplier centred between `lo` and `hi`.
pub fn wave_mult(x: i32, y: i32, frame: u32, lo: u8, hi: u8) -> u8 {
    let phase = (x as u32)
        .wrapping_mul(16)
        .wrapping_add((y as u32).wrapping_mul(8))
        .wrapping_add(frame.wrapping_mul(4))
        & 0xFF;
    let s = SIN_U8[phase as usize] as u16;
    let span = (hi - lo) as u16;
    (lo as u16 + (s * span) / 255) as u8
}

/// Multiply an RGB colour by an 8-bit brightness multiplier (256 = full).
pub fn scale_rgb(c: (u8, u8, u8), m: u8) -> (u8, u8, u8) {
    let m = m as u16 + 1;
    let r = ((c.0 as u16) * m) >> 8;
    let g = ((c.1 as u16) * m) >> 8;
    let b = ((c.2 as u16) * m) >> 8;
    (r as u8, g as u8, b as u8)
}
