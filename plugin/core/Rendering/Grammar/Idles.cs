// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The Watchline Embers idle family (docs/flag-grammar.md §7) — one ember
// primitive (core, shoulders at (m*3)>>3, halo at m>>2), parameterised per
// state. Inset rule: no idle pixel touches row/column 0 or 31, so idles are
// structurally distinct from the 1-px warning frames. The firmware fallback
// (roaming amber ember) lives in firmware/src/screens.rs, not here.

namespace Uniflag.Rendering.Grammar
{
    public static class Idles
    {
        /// <summary>
        /// Connected-idle (§7b): docked teal beacon, bottom-centre. Stream
        /// alive, no game. Fills the panel itself — called standalone by the
        /// renderer loop's connected-idle input mode.
        /// </summary>
        public static void PaintConnectedIdle(FrameBuffer s, uint frame)
        {
            Painter.Fill(s, Palette.Black);
            byte m = (byte)(10 + Anim.Breathe(frame, 240) * 14 / 255);
            byte shoulder = (byte)((m * 3) >> 3);
            byte halo = (byte)(m >> 2);
            s.SetPixel(15, 30, Anim.ScaleRgb(Palette.Teal, m));
            s.SetPixel(16, 30, Anim.ScaleRgb(Palette.Teal, m));
            s.SetPixel(14, 30, Anim.ScaleRgb(Palette.Teal, shoulder));
            s.SetPixel(17, 30, Anim.ScaleRgb(Palette.Teal, shoulder));
            s.SetPixel(15, 29, Anim.ScaleRgb(Palette.Teal, halo));
            s.SetPixel(16, 29, Anim.ScaleRgb(Palette.Teal, halo));
        }

        /// <summary>
        /// Session idles (§7 c1/c2), painted over an already-black panel when
        /// no signal holds the field. Racing/Paused: fully static grey corner
        /// ticks — zero motion, the quietest state (a dead stream self-reveals
        /// via the firmware fallback). Other sessions: violet anti-phase
        /// flankers whose aggregate luminance is constant to ±1 LSB.
        /// </summary>
        public static void PaintSessionIdle(FrameBuffer s, Session session, uint frame)
        {
            if (session == Session.Racing || session == Session.Paused)
            {
                s.SetPixel(1, 30, new Rgb(8, 8, 8));
                s.SetPixel(2, 30, new Rgb(4, 4, 4));
                s.SetPixel(29, 30, new Rgb(4, 4, 4));
                s.SetPixel(30, 30, new Rgb(8, 8, 8));
                return;
            }

            byte left = (byte)(8 + Anim.Breathe(frame, 240) * 12 / 255);
            byte right = (byte)(8 + Anim.Breathe(unchecked(frame + 120), 240) * 12 / 255);
            DrawEmber(s, 8, left);
            DrawEmber(s, 23, right);
        }

        private static void DrawEmber(FrameBuffer s, int x, byte m)
        {
            byte shoulder = (byte)((m * 3) >> 3);
            byte halo = (byte)(m >> 2);
            s.SetPixel(x, 30, Anim.ScaleRgb(Palette.Violet, m));
            s.SetPixel(x - 1, 30, Anim.ScaleRgb(Palette.Violet, shoulder));
            s.SetPixel(x + 1, 30, Anim.ScaleRgb(Palette.Violet, shoulder));
            s.SetPixel(x, 29, Anim.ScaleRgb(Palette.Violet, halo));
        }
    }
}
