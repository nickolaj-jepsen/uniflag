// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Board-slot painters (docs/flag-grammar.md §6.2, §8). One chrome for every
// board: solid black backing, 1-px white outline, white 7×11 glyphs, centred
// both axes — glyph gap 1, horizontal padding 2, vertical padding 3. Boards
// are static (motion lives in the field); the gantry is the one licensed
// animated-content exception.

namespace Uniflag.Rendering.Grammar
{
    public static class Boards
    {
        private static readonly byte[][] ScRow = { Font7x11.S, Font7x11.C };
        private static readonly byte[][] DqRow = { Font7x11.D, Font7x11.Q };
        private static readonly byte[][] XRow = { Font7x11.X };
        private static readonly byte[][] TenRow = { Font7x11.One, Font7x11.Zero };
        private static readonly byte[][] FiveRow = { Font7x11.Five };

        private const int GantryContentWidth = 24;
        private const int GantryContentHeight = 4;

        public static void Paint(FrameBuffer s, in Composition comp, in SlotEnvelope env, in SignalState state, uint frame)
        {
            if (env.Phase == EnvelopePhase.Hidden)
            {
                return;
            }
            if (env.Phase == EnvelopePhase.FadeOut && comp.Takeover)
            {
                // The takeover's own onset covers the departing board.
                return;
            }

            BoardKind kind = env.Phase == EnvelopePhase.FadeOut ? (BoardKind)env.PriorKind : comp.Board;
            byte value = env.Phase == EnvelopePhase.FadeOut ? env.PriorValue : comp.BoardValue;

            byte[][] glyphs = GlyphsFor(kind, value);
            int contentW = glyphs != null
                ? TextEngine.MeasureRow(glyphs.Length, 1)
                : kind == BoardKind.StartGantry ? GantryContentWidth : Font7x11.GlyphWidth;
            int contentH = kind == BoardKind.StartGantry ? GantryContentHeight : Font7x11.GlyphHeight;
            int boxW = contentW + 6;
            int boxH = contentH + 8;
            int x0 = (FrameBuffer.Width - boxW) / 2;
            int y0 = (FrameBuffer.Height - boxH) / 2;
            int x1 = x0 + boxW - 1;
            int y1 = y0 + boxH - 1;

            if (env.Phase == EnvelopePhase.Flash && env.Age < 2)
            {
                Painter.FillRect(s, x0, y0, x1, y1, Palette.White);
                return;
            }

            Painter.FillRect(s, x0, y0, x1, y1, Palette.Black);
            Painter.OutlineRect(s, x0, y0, x1, y1, Palette.White);

            if (glyphs != null)
            {
                TextEngine.DrawRow(s, glyphs, 1, x0 + 3, y0 + 4, Palette.White);
            }
            else if (kind == BoardKind.StartGantry)
            {
                PaintGantryLights(s, x0 + 3, y0 + 4, state.StartPhase, frame);
            }
            else if (kind == BoardKind.MeatballFlag)
            {
                PaintDiscIcon(s, x0, y0, x1, y1);
            }

            if (env.Phase == EnvelopePhase.Flash)
            {
                Painter.BlendTowardWhite(s, x0, y0, x1, y1, Painter.FlashBlend(env.Age));
            }
            else if (env.Phase == EnvelopePhase.FadeOut)
            {
                Painter.ScaleRect(s, x0, y0, x1, y1, Painter.FadeScale(env.Age));
            }
        }

        private static byte[][] GlyphsFor(BoardKind kind, byte value)
        {
            switch (kind)
            {
                case BoardKind.SafetyCar:
                    return ScRow;
                case BoardKind.Disqualified:
                    return DqRow;
                case BoardKind.BlackFlag:
                    return XRow;
                case BoardKind.Countdown:
                    // The adapter vocabulary is exactly 10 and 5 (§6.2); an
                    // impossible value paints the bare chrome, never a lie.
                    return value == 10 ? TenRow : value == 5 ? FiveRow : null;
                default:
                    return null;
            }
        }

        private static void PaintGantryLights(FrameBuffer s, int x, int y, StartPhase phase, uint frame)
        {
            // Five 4×4 lights, 1-px gaps (§6.4). Ready: amber standby breathe;
            // Set: all five red; Go (and fade): dark sockets.
            Rgb readyColor = default;
            int litCount = 0;
            if (phase == StartPhase.Ready)
            {
                readyColor = Anim.ScaleRgb(Palette.Amber, (byte)(80 + Anim.Breathe(frame, 240) * 80 / 255));
                litCount = 5;
            }
            else if (phase == StartPhase.Set)
            {
                litCount = 5;
            }

            for (int i = 0; i < 5; i++)
            {
                Rgb color = i < litCount
                    ? (phase == StartPhase.Ready ? readyColor : Palette.Red)
                    : Palette.GantrySocket;
                Painter.FillRect(s, x + i * 5, y, x + i * 5 + 3, y + 3, color);
            }
        }

        private static void PaintDiscIcon(FrameBuffer s, int x0, int y0, int x1, int y1)
        {
            // Orange disc, radius 4.5 px in the half-pixel metric, centred in
            // the box — the demoted meatball keeps its true form.
            int cx2 = x0 + x1;
            int cy2 = y0 + y1;
            for (int y = y0 + 1; y < y1; y++)
            {
                for (int x = x0 + 1; x < x1; x++)
                {
                    int dx2 = 2 * x - cx2;
                    int dy2 = 2 * y - cy2;
                    if (dx2 * dx2 + dy2 * dy2 <= 81)
                    {
                        s.SetPixel(x, y, Palette.Orange);
                    }
                }
            }
        }
    }
}
