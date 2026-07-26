// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Frame-slot painters (docs/flag-grammar.md §6.3): 1-px perimeter
// advisories. Incident-limit = solid red ring; furled = alternating 4-px
// white dashes. Envelope: 2 Hz blink through the attention window, settling
// to a steady dim ring.

namespace Uniflag.Rendering.Grammar
{
    public static class Frames
    {
        public static void Paint(FrameBuffer s, in Composition comp, in SlotEnvelope env, uint frame)
        {
            if (env.Phase == EnvelopePhase.Hidden)
            {
                return;
            }
            if (env.Phase == EnvelopePhase.FadeOut && comp.Takeover)
            {
                return;
            }

            FrameKind kind = env.Phase == EnvelopePhase.FadeOut ? (FrameKind)env.PriorKind : comp.Frame;
            switch (env.Phase)
            {
                case EnvelopePhase.Flash:
                    DrawRing(s, kind, 255, env.Age < 2 ? (byte)255 : Painter.FlashBlend(env.Age));
                    return;
                case EnvelopePhase.Attention:
                    if (Anim.Strobe60(frame, 2))
                    {
                        DrawRing(s, kind, 255, 0);
                    }
                    return;
                case EnvelopePhase.Ambient:
                    DrawRing(s, kind, 60, 0);
                    return;
                case EnvelopePhase.FadeOut:
                    DrawRing(s, kind, (byte)(60 * (EnvelopeTracker.FadeFrames - env.Age) / EnvelopeTracker.FadeFrames), 0);
                    return;
            }
        }

        private static void DrawRing(FrameBuffer s, FrameKind kind, byte m, byte whiteBlend)
        {
            Rgb baseColor = kind == FrameKind.Incident ? Palette.Red : Palette.White;
            Rgb c = Anim.ScaleRgb(baseColor, m);
            if (whiteBlend > 0)
            {
                c = new Rgb(
                    (byte)(c.R + (255 - c.R) * whiteBlend / 255),
                    (byte)(c.G + (255 - c.G) * whiteBlend / 255),
                    (byte)(c.B + (255 - c.B) * whiteBlend / 255));
            }

            int max = FrameBuffer.Width - 1;
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    if (x != 0 && x != max && y != 0 && y != max)
                    {
                        continue;
                    }
                    if (kind == FrameKind.Furled && !FurledDashLit(x, y, max))
                    {
                        continue;
                    }
                    s.SetPixel(x, y, c);
                }
            }
        }

        /// <summary>
        /// Alternating 4-px dashes along the clockwise ring walk: top row →
        /// right column → bottom row → left column (§6.3).
        /// </summary>
        private static bool FurledDashLit(int x, int y, int max)
        {
            int pos;
            if (y == 0)
            {
                pos = x;
            }
            else if (x == max)
            {
                pos = max + y;
            }
            else if (y == max)
            {
                pos = 2 * max + (max - x);
            }
            else
            {
                pos = 3 * max + (max - y);
            }
            return ((pos >> 2) & 1) == 0;
        }
    }
}
