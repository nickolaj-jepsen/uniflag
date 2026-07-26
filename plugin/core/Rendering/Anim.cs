// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Integer-only animation primitives for the Grammar renderer
// (docs/flag-grammar.md §4/§6). Must stay float-free and every division
// truncates (u32/u16 semantics): the painters are tuned against this exact
// rounding, so "improving" it silently shifts every animation that uses it.

using System;

namespace Uniflag.Rendering
{
    /// <summary>Pure integer-function animation primitives.</summary>
    public static class Anim
    {
        /// <summary>
        /// 8-bit unsigned sine indexed by a phase 0..=255 (one full period).
        /// Centred at 128: <c>SinU8[0] == 128</c>, peak 255 at index 64,
        /// trough 1 at index 192. Integer Bhaskara I formula.
        /// </summary>
        public static readonly byte[] SinU8 = BuildSinLut();

        private static byte[] BuildSinLut()
        {
            var lut = new byte[256];
            for (uint k = 0; k < 256; k++)
            {
                bool neg = k >= 128;
                uint a = k % 128;
                uint q = a * (128 - a);
                uint mag = 16 * q * 127 / (81920 - 4 * q);
                if (neg)
                {
                    // saturating 128 - mag; mag <= 127 so the clamp never fires.
                    byte m = unchecked((byte)mag);
                    lut[k] = (byte)(m > 128 ? 0 : 128 - m);
                }
                else
                {
                    // mag <= 127, always in range.
                    lut[k] = unchecked((byte)(128 + mag));
                }
            }
            return lut;
        }

        /// <summary>
        /// 60 fps strobe with ~60 % duty; <c>true</c> during the on-phase.
        /// <paramref name="hz"/> must be &gt;= 1. The exact
        /// <c>(period * 6 + 5) / 10</c> duty rounding is load-bearing — do not
        /// simplify.
        /// </summary>
        public static bool Strobe60(uint frame, uint hz)
        {
            uint period = Math.Max(60u / hz, 1u);
            uint on = (period * 6 + 5) / 10;
            return frame % period < on;
        }

        /// <summary>
        /// Sine envelope over <paramref name="periodFrames"/> frames. Output
        /// range 1..=255; starts at 128 when <c>frame % period == 0</c>, peaks
        /// at the quarter period, troughs at three quarters.
        /// </summary>
        public static byte Breathe(uint frame, uint periodFrames)
        {
            uint p = frame % periodFrames * 256 / periodFrames;
            return SinU8[p & 0xFF];
        }

        /// <summary>
        /// Per-pixel diagonal cloth-wave brightness multiplier. The phase is
        /// wrapping u32 arithmetic — <c>(16x + 8y + 4*frame) mod 2^32</c>, low
        /// byte taken. Result is in <c>[lo, hi]</c>; callers always satisfy
        /// <c>hi &gt;= lo</c>.
        /// </summary>
        public static byte WaveMult(int x, int y, uint frame, byte lo, byte hi)
        {
            uint phase = unchecked((uint)x * 16u + (uint)y * 8u + frame * 4u) & 0xFF;
            int s = SinU8[phase];
            int span = hi - lo;
            // max 255 * 105, never overflows — plain int math suffices.
            return unchecked((byte)(lo + s * span / 255));
        }

        /// <summary>
        /// Multiply a colour by an 8-bit brightness multiplier: per channel
        /// <c>(c * (m + 1)) &gt;&gt; 8</c>. <c>m = 255</c> is the identity;
        /// <c>m = 0</c> yields black.
        /// </summary>
        public static Rgb ScaleRgb(Rgb c, byte m)
        {
            int mm = m + 1;
            return new Rgb(
                (byte)((c.R * mm) >> 8),
                (byte)((c.G * mm) >> 8),
                (byte)((c.B * mm) >> 8));
        }

        /// <summary>
        /// Floor modulus. C#'s <c>%</c> is a
        /// remainder and goes negative for negative dividends:
        /// <c>-5 % 32 == -5</c> but <c>FloorMod(-5, 32) == 27</c>. All effect
        /// call sites use <paramref name="b"/> &gt; 0, where the two coincide.
        /// </summary>
        public static int FloorMod(int a, int b)
        {
            int r = a % b;
            if (r != 0 && (r ^ b) < 0)
            {
                r += b;
            }
            return r;
        }
    }
}
