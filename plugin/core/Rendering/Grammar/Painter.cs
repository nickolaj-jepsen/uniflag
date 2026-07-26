// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Grammar renderer entry point (docs/flag-grammar.md §4–§5). Paint order is
// field → board → frame. All animation state arrives via the Envelopes —
// Paint itself is a pure function of (composition, envelopes, state, frame).

namespace Uniflag.Rendering.Grammar
{
    public static class Painter
    {
        public static void Paint(FrameBuffer s, in Composition comp, in Envelopes env, in SignalState state, uint frame)
        {
            if (!comp.Connected)
            {
                Fill(s, Palette.Black);
                return;
            }

            PaintFieldSlot(s, comp, env.Field, state, frame);
            Boards.Paint(s, comp, env.Board, state, frame);
            Frames.Paint(s, comp, env.Frame, frame);
        }

        private static void PaintFieldSlot(FrameBuffer s, in Composition comp, in SlotEnvelope env, in SignalState state, uint frame)
        {
            switch (env.Phase)
            {
                case EnvelopePhase.Hidden:
                    Fill(s, Palette.Black);
                    Idles.PaintSessionIdle(s, state.Session, frame);
                    return;

                case EnvelopePhase.Flash:
                    if (comp.Field == FieldKind.Green)
                    {
                        // Signature onset: green's sweep replaces the flash (R4).
                        Fields.Paint(s, FieldKind.Green, comp.FieldTier, state, frame, env.Age, attention: true);
                        return;
                    }
                    if (env.Age < 2)
                    {
                        Fill(s, Palette.White);
                        return;
                    }
                    Fields.Paint(s, comp.Field, comp.FieldTier, state, frame, env.Age, attention: true);
                    BlendTowardWhite(s, 0, 0, FrameBuffer.Width - 1, FrameBuffer.Height - 1, FlashBlend(env.Age));
                    return;

                case EnvelopePhase.Attention:
                    Fields.Paint(s, comp.Field, comp.FieldTier, state, frame, env.Age, attention: true);
                    return;

                case EnvelopePhase.Ambient:
                    Fields.Paint(s, comp.Field, comp.FieldTier, state, frame, env.Age, attention: false);
                    return;

                case EnvelopePhase.FadeOut:
                    Fields.Paint(s, (FieldKind)env.PriorKind, env.PriorTier, state, frame, env.Age, attention: false);
                    ScaleAll(s, FadeScale(env.Age));
                    return;
            }
        }

        /// <summary>White-blend weight for flash ages 2..7 (§4: ages 0-1 are solid white).</summary>
        internal static byte FlashBlend(uint age) => (byte)((7 - age) * 255 / 6);

        /// <summary>Brightness multiplier for fade-out ages 0..14: 255 down to 17.</summary>
        internal static byte FadeScale(uint age) => (byte)((EnvelopeTracker.FadeFrames - age) * 17);

        internal static void Fill(FrameBuffer s, Rgb c)
        {
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    s.SetPixel(x, y, c);
                }
            }
        }

        internal static void WaveFill(FrameBuffer s, Rgb c, uint frame, byte lo, byte hi)
        {
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    s.SetPixel(x, y, Anim.ScaleRgb(c, Anim.WaveMult(x, y, frame, lo, hi)));
                }
            }
        }

        internal static void ScaleAll(FrameBuffer s, byte m) =>
            ScaleRect(s, 0, 0, FrameBuffer.Width - 1, FrameBuffer.Height - 1, m);

        internal static void ScaleRect(FrameBuffer s, int x0, int y0, int x1, int y1, byte m)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    s.SetPixel(x, y, Anim.ScaleRgb(s.GetPixel(x, y), m));
                }
            }
        }

        internal static void FillRect(FrameBuffer s, int x0, int y0, int x1, int y1, Rgb c)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    s.SetPixel(x, y, c);
                }
            }
        }

        internal static void OutlineRect(FrameBuffer s, int x0, int y0, int x1, int y1, Rgb c)
        {
            for (int x = x0; x <= x1; x++)
            {
                s.SetPixel(x, y0, c);
                s.SetPixel(x, y1, c);
            }
            for (int y = y0; y <= y1; y++)
            {
                s.SetPixel(x0, y, c);
                s.SetPixel(x1, y, c);
            }
        }

        /// <summary>Blend every pixel in the rect toward white with weight <paramref name="w"/> (255 = solid white).</summary>
        internal static void BlendTowardWhite(FrameBuffer s, int x0, int y0, int x1, int y1, byte w)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Rgb c = s.GetPixel(x, y);
                    s.SetPixel(
                        x,
                        y,
                        new Rgb(
                            (byte)(c.R + (255 - c.R) * w / 255),
                            (byte)(c.G + (255 - c.G) * w / 255),
                            (byte)(c.B + (255 - c.B) * w / 255)));
                }
            }
        }
    }
}
