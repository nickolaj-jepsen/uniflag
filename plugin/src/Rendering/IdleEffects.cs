// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Plugin connected-idle painter — docs/effects-spec.md §7b, implemented
// bit-exactly with the §2 primitives. Deliberately a separate class from
// Effects: the golden corpus (testdata/frames/) pins Effects.Paint
// byte-for-byte, so no new code path may enter it. Pinned by its own unit
// tests instead.

namespace Uniflag.Rendering
{
    /// <summary>
    /// Idle renderings outside the golden-frozen <see cref="Effects"/>
    /// dispatch. <see cref="PaintConnectedIdle"/> is a pure function of the
    /// frame counter, like every effect painter.
    /// </summary>
    public static class IdleEffects
    {
        // BLUE of the shared palette (docs/effects-spec.md §3). Effects'
        // palette is private and stays untouched by contract.
        private static readonly Rgb Blue = new Rgb(0, 64, 255);

        // 240 frames = 0.25 Hz at the 60 fps renderer clock — the same
        // period as the race-idle pulse, but blue and bottom-centre so the
        // two states stay distinguishable at a glance (§7 rationale).
        private const uint BreathePeriodFrames = 240;

        /// <summary>
        /// The §7b connected-idle marker (stream alive, no game session):
        /// every pixel black except the bottom-centre pair (15,31) and
        /// (16,31), which breathe dim blue —
        /// <c>m = 8 + breathe(f, 240) * 16 / 255</c> (integer math, range
        /// 8..=24), colour <c>scale_rgb(BLUE, m)</c>: (0,2,8) at the trough
        /// (frame 180 of each period) to (0,6,24) at the peak (frame 60).
        /// </summary>
        public static void PaintConnectedIdle(FrameBuffer s, uint frame)
        {
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    s.SetPixel(x, y, 0, 0, 0);
                }
            }
            // Rust would compute this in u16; the intermediate (255 * 16)
            // fits comfortably, so plain int math is value-identical.
            byte m = (byte)(8 + Anim.Breathe(frame, BreathePeriodFrames) * 16 / 255);
            Rgb c = Anim.ScaleRgb(Blue, m);
            s.SetPixel(15, 31, c);
            s.SetPixel(16, 31, c);
        }
    }
}
