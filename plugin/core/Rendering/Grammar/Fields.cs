// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Field-slot painters (docs/flag-grammar.md §6.1). Tier motion is generic —
// Alert = 2 Hz pulse (ambient vs ambient scaled to ~35 %), Urgent = 4 Hz
// strobe (ambient vs black) — except for the licensed signatures: blue's
// sweep band IS its tier motion, green's onset sweep replaces its first 30
// attention frames, checkered expresses attention as scroll speed, and
// debris is Tier-0 only. Ambient forms double as the settled (post-window)
// renderings per rule R5.

namespace Uniflag.Rendering.Grammar
{
    public static class Fields
    {
        public static void Paint(FrameBuffer s, FieldKind kind, Tier tier, in SignalState state, uint frame, uint age, bool attention)
        {
            switch (kind)
            {
                case FieldKind.Yellow:
                case FieldKind.Red:
                case FieldKind.White:
                case FieldKind.Meatball:
                    Modulated(s, kind, tier, state, frame, attention);
                    return;

                case FieldKind.Black:
                    if (state.Disqualified)
                    {
                        // Terminal variant: steady solid X, no motion (§6.1).
                        BlackField(s, 200);
                        return;
                    }
                    if (attention && !Anim.Strobe60(frame, 2))
                    {
                        BlackField(s, 90);
                        return;
                    }
                    BlackField(s, attention ? (byte)255 : AmbientXBrightness(frame));
                    return;

                case FieldKind.Green:
                    if (attention && age < 30)
                    {
                        GreenOnsetSweep(s, age);
                        return;
                    }
                    Modulated(s, kind, tier, state, frame, attention);
                    return;

                case FieldKind.Blue:
                    Painter.WaveFill(s, Palette.Blue, frame, 150, 255);
                    if (attention && tier != Tier.Ambient)
                    {
                        BlueSweepBand(s, frame, tier);
                    }
                    return;

                case FieldKind.Checkered:
                    CheckeredField(s, frame, fast: attention);
                    return;

                case FieldKind.Debris:
                    DebrisField(s, frame);
                    return;

                default:
                    Painter.Fill(s, Palette.Black);
                    return;
            }
        }

        private static void Modulated(FrameBuffer s, FieldKind kind, Tier tier, in SignalState state, uint frame, bool attention)
        {
            if (attention && tier == Tier.Urgent && !Anim.Strobe60(frame, 4))
            {
                Painter.Fill(s, Palette.Black);
                return;
            }
            Ambient(s, kind, frame);
            if (attention && tier == Tier.Alert && !Anim.Strobe60(frame, 2))
            {
                Painter.ScaleAll(s, 90);
            }
        }

        private static void Ambient(FrameBuffer s, FieldKind kind, uint frame)
        {
            switch (kind)
            {
                case FieldKind.Yellow:
                    Painter.WaveFill(s, Palette.Yellow, frame, 150, 255);
                    return;
                case FieldKind.Red:
                    Painter.WaveFill(s, Palette.Red, frame, 150, 255);
                    return;
                case FieldKind.White:
                    Painter.WaveFill(s, Palette.White, frame, 220, 255);
                    return;
                case FieldKind.Green:
                    Painter.WaveFill(s, Palette.Green, frame, 220, 255);
                    return;
                case FieldKind.Meatball:
                    MeatballField(s, frame);
                    return;
            }
        }

        /// <summary>Ambient X brightness: calm breathe 80..180 (§6.1).</summary>
        private static byte AmbientXBrightness(uint frame) =>
            (byte)(80 + Anim.Breathe(frame, 240) * 100 / 255);

        private static void BlackField(FrameBuffer s, byte m)
        {
            var x = new Rgb(m, m, m);
            for (int py = 0; py < FrameBuffer.Height; py++)
            {
                for (int px = 0; px < FrameBuffer.Width; px++)
                {
                    bool onX = System.Math.Abs(px - py) <= 1
                        || System.Math.Abs(px + py - 31) <= 1;
                    s.SetPixel(px, py, onX ? x : Palette.Black);
                }
            }
        }

        private static void MeatballField(FrameBuffer s, uint frame)
        {
            byte m = (byte)(150 + Anim.Breathe(frame, 240) * 105 / 255);
            Rgb disc = Anim.ScaleRgb(Palette.Orange, m);
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    int dx2 = 2 * x - 31;
                    int dy2 = 2 * y - 31;
                    // Half-pixel metric, radius 10.0 (§6.1).
                    s.SetPixel(x, y, dx2 * dx2 + dy2 * dy2 <= 400 ? disc : Palette.Black);
                }
            }
        }

        private static void GreenOnsetSweep(FrameBuffer s, uint age)
        {
            int pos = (int)(age * 40 / 30) - 4;
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    bool inBand = System.Math.Abs(x - pos) < 4;
                    s.SetPixel(x, y, inBand ? Palette.GreenSweepBand : Palette.Green);
                }
            }
        }

        private static void BlueSweepBand(FrameBuffer s, uint frame, Tier tier)
        {
            uint step = tier == Tier.Urgent ? 1u : 2u;
            int pos = (int)(frame / step % 32);
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    if (Anim.FloorMod(x - pos, 32) < 4)
                    {
                        s.SetPixel(x, y, Palette.Blue);
                    }
                }
            }
        }

        private static void CheckeredField(FrameBuffer s, uint frame, bool fast)
        {
            int off = (int)(frame / (fast ? 2u : 8u));
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    int cell = ((x + off) / 4 + (y + off) / 4) & 1;
                    s.SetPixel(x, y, cell == 0 ? Palette.Black : Palette.White);
                }
            }
        }

        private static void DebrisField(FrameBuffer s, uint frame)
        {
            int off = (int)(frame / 16);
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    int stripe = ((x + y + off) / 4) & 1;
                    s.SetPixel(x, y, stripe == 0 ? Palette.Yellow : Palette.Red);
                }
            }
        }
    }
}
