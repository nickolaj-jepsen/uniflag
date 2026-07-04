// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Per-flag paint functions against docs/effects-spec.md. Everything is
// integer math: the golden corpus under testdata/frames/ pins every effect
// exactly, so any deviation in division rounding, wrapping or saturation is
// a conformance failure, not a style choice.
//
// The penalty suite (slowdown board, meatball board, DT/SG black-flag
// markers, furled warning accent) is pinned by its own separate corpus
// under testdata/frames-plugin/. Every penalty code path is unreachable
// while RenderState's penalty fields hold their defaults, so the two
// corpora never contend over the same tuples.

using System;

namespace Uniflag.Rendering
{
    /// <summary>
    /// The whole-panel renderer: <see cref="Paint"/> recomputes all 32×32
    /// pixels from scratch every tick as a pure function of
    /// (state, frame, flagAge, connected).
    /// </summary>
    public static class Effects
    {
        private const int Width = FrameBuffer.Width;
        private const int Height = FrameBuffer.Height;

        private static readonly Rgb Black = new Rgb(0, 0, 0);
        private static readonly Rgb Yellow = new Rgb(255, 220, 0);
        private static readonly Rgb Blue = new Rgb(0, 64, 255);
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Green = new Rgb(0, 220, 0);
        private static readonly Rgb White = new Rgb(255, 255, 255);
        private static readonly Rgb Orange = new Rgb(255, 90, 0);
        private static readonly Rgb SectorDim = new Rgb(40, 30, 0);

        private const int SectorBandHeight = 2;

        // Three 10-px sector segments with 1-px gaps at cols 10 and 21;
        // inclusive ranges S1 0..=9, S2 11..=20, S3 22..=31.
        private static readonly int[] SegmentLo = { 0, 11, 22 };
        private static readonly int[] SegmentHi = { 9, 20, 31 };

        // Caution-board geometry: 2-px border width.
        private const int CautionBorder = 2;

        /// <summary>
        /// Recompute the whole panel. <paramref name="frame"/> is the 60 fps
        /// tick counter; <paramref name="flagAge"/> is frames since the flag
        /// last changed (consumed by red onset, green onset and the ready orb).
        /// </summary>
        public static void Paint(FrameBuffer s, RenderState state, uint frame, uint flagAge, bool connected)
        {
            switch (Precedence.Select(state, connected))
            {
                case RenderLayer.Disconnected:
                    Fill(s, Black);
                    return; // no overlays
                case RenderLayer.RedFlag:
                    PaintRed(s, state.Wave, frame, flagAge);
                    break;
                case RenderLayer.VscBoard:
                    PaintVsc(s, frame);
                    break;
                case RenderLayer.SafetyCarBoard:
                    PaintSafetyCar(s, frame);
                    break;
                case RenderLayer.SlowdownBoard:
                    PaintSlowdown(s, state.Slowdown, frame);
                    break;
                case RenderLayer.MeatballBoard:
                    PaintMeatball(s, frame);
                    break;
                case RenderLayer.YellowFlag:
                    PaintYellow(s, state.Wave, frame);
                    break;
                case RenderLayer.BlueFlag:
                    PaintBlue(s, state.Wave, frame);
                    break;
                case RenderLayer.GreenFlag:
                    PaintGreen(s, state.Wave, frame, flagAge);
                    break;
                case RenderLayer.WhiteFlag:
                    PaintWhite(s, state.Wave, frame);
                    break;
                case RenderLayer.BlackFlag:
                    PaintBlackFlag(s, frame, state.BlackDetail);
                    break;
                case RenderLayer.OrangeFlag:
                    PaintOrange(s, state.Wave, frame);
                    break;
                case RenderLayer.CheckeredFlag:
                    PaintCheckered(s, frame);
                    break;
                case RenderLayer.StartLightsBoard:
                    PaintStartLights(s, state.StartLights, frame);
                    break;
                case RenderLayer.DebrisBoard:
                    PaintDebris(s, frame);
                    break;
                case RenderLayer.RaceIdle:
                    PaintRaceIdle(s, frame);
                    break;
                case RenderLayer.ReadyOrb:
                    PaintReady(s, frame, flagAge);
                    break;
            }
            if (Precedence.IncidentWarningVisible(state, connected))
            {
                // Painted before the two edge overlays below so the sector
                // band (bottom rows) and furled tile (top-centre) stay
                // legible where they meet the frame's edges.
                PaintIncidentFrame(s, frame);
            }
            if (Precedence.SectorBandVisible(state, connected))
            {
                PaintSectorBand(s, state.Sectors, state.Wave, frame);
            }
            if (Precedence.FurledAccentVisible(state, connected))
            {
                // Painted last: the accent (rows 0..6) and the sector band
                // (rows 30..31) are disjoint, so order between the two
                // overlays is cosmetic — but both sit over the base layer.
                PaintFurledAccent(s, frame);
            }
        }

        // ---------------------------------------------------------------
        // Per-flag base layers.
        // ---------------------------------------------------------------

        /// <summary>Yellow (spec §5.1).</summary>
        private static void PaintYellow(FrameBuffer s, WaveLevel wave, uint frame)
        {
            switch (wave)
            {
                case WaveLevel.None:
                    // Static: pronounced cloth-wave overlay (≈59–100 %).
                    PaintWithWave(s, Yellow, frame, 150, 255);
                    break;
                case WaveLevel.Single:
                    // Whole-panel strobe at 2 Hz.
                    Fill(s, Anim.Strobe60(frame, 2) ? Yellow : Black);
                    break;
                default:
                    // Double: anti-diagonal split-triangle alternation, 4 Hz.
                    // Exactly one triangle lit; the dividing line (x + y == 31)
                    // blinks with the upper-left half.
                    const uint Period = 15;
                    bool upperOn = frame % Period < Period / 2;
                    FillWith(s, (x, y) =>
                    {
                        bool upper = x + y <= Width - 1;
                        bool on = upper ? upperOn : !upperOn;
                        return on ? Yellow : Black;
                    });
                    break;
            }
        }

        /// <summary>Red (spec §5.2).</summary>
        private static void PaintRed(FrameBuffer s, WaveLevel wave, uint frame, uint flagAge)
        {
            // Sharp white onset flash for 4 frames, regardless of wave level.
            const uint OnsetFrames = 4;
            if (flagAge < OnsetFrames)
            {
                byte progress = unchecked((byte)(flagAge * 255 / OnsetFrames));
                byte gb = (byte)(255 - progress);
                Fill(s, new Rgb(255, gb, gb));
                return;
            }
            uint strobeHz = wave switch
            {
                WaveLevel.Single => 2u,
                WaveLevel.Double => 4u,
                _ => 0u,
            };
            if (strobeHz != 0)
            {
                // Waved: clean on/off strobe, no cloth-wave overlay.
                Fill(s, Anim.Strobe60(frame, strobeHz) ? Red : Black);
                return;
            }
            PaintWithWave(s, Red, frame, 150, 255);
        }

        /// <summary>Blue (spec §5.3).</summary>
        private static void PaintBlue(FrameBuffer s, WaveLevel wave, uint frame)
        {
            // Cloth-wave overlay; under wave levels, an additional brighter
            // 4-px band drifts L→R, wrapping around the right edge.
            uint sweepStepFrames = wave switch
            {
                WaveLevel.Single => 2u, // 1 px / 2 frames
                WaveLevel.Double => 1u, // 1 px / frame
                _ => 0u,                // no sweep
            };

            if (sweepStepFrames == 0)
            {
                PaintWithWave(s, Blue, frame, 150, 255);
                return;
            }

            int sweepPos = (int)(frame / sweepStepFrames % Width);
            FillWith(s, (x, y) =>
            {
                // rem_euclid hazard site (spec §2.6): x - sweepPos is
                // frequently negative; C# '%' would go negative here.
                byte m = Anim.FloorMod(x - sweepPos, Width) < 4
                    ? (byte)255
                    : Anim.WaveMult(x, y, frame, 150, 255);
                return Anim.ScaleRgb(Blue, m);
            });
        }

        /// <summary>Green (spec §5.4).</summary>
        private static void PaintGreen(FrameBuffer s, WaveLevel wave, uint frame, uint flagAge)
        {
            // Onset: sweep a bright band L→R once over 30 frames, ignoring
            // wave level. pos starts at -4 (entirely off-panel at age 0) —
            // signed math throughout.
            const uint SweepFrames = 30;
            const int BandHalf = 4;
            if (flagAge < SweepFrames)
            {
                const int Span = Width + BandHalf * 2;
                int pos = (int)flagAge * Span / (int)SweepFrames - BandHalf;
                var bright = new Rgb(200, 255, 200);
                FillWith(s, (x, y) => Math.Abs(x - pos) < BandHalf ? bright : Green);
                return;
            }

            // Strobe under wave levels (rarely seen — green is normally static).
            uint strobeHz = wave switch
            {
                WaveLevel.Single => 2u,
                WaveLevel.Double => 4u,
                _ => 0u,
            };
            if (strobeHz != 0 && !Anim.Strobe60(frame, strobeHz))
            {
                Fill(s, Black);
                return;
            }
            // On-phase (and static) keeps the cloth wave, unlike yellow/red.
            PaintWithWave(s, Green, frame, 220, 255);
        }

        /// <summary>White (spec §5.5).</summary>
        private static void PaintWhite(FrameBuffer s, WaveLevel wave, uint frame)
        {
            uint strobeHz = wave switch
            {
                WaveLevel.Single => 3u,
                WaveLevel.Double => 5u,
                _ => 0u,
            };
            if (strobeHz != 0 && !Anim.Strobe60(frame, strobeHz))
            {
                Fill(s, Black);
                return;
            }
            PaintWithWave(s, White, frame, 220, 255);
        }

        /// <summary>Orange / meatball (spec §5.7).</summary>
        private static void PaintOrange(FrameBuffer s, WaveLevel wave, uint frame)
        {
            // Rotating quartered black/orange; two adjacent quadrants lit,
            // rotating clockwise one step per period.
            uint period = wave switch
            {
                WaveLevel.Single => 24u, // 400 ms
                WaveLevel.Double => 12u, // 200 ms
                _ => 90u,                // 1.5 s
            };
            int step = (int)(frame / period % 4);
            const int HalfW = Width / 2;
            const int HalfH = Height / 2;
            FillWith(s, (x, y) =>
            {
                // Quadrants numbered clockwise from TL: 0=TL, 1=TR, 2=BR, 3=BL.
                int q = y < HalfH ? (x < HalfW ? 0 : 1) : (x < HalfW ? 3 : 2);
                bool on = q == step || q == (step + 1) % 4;
                return on ? Orange : Black;
            });
        }

        /// <summary>Checkered (spec §5.8).</summary>
        private static void PaintCheckered(FrameBuffer s, uint frame)
        {
            // 4×4 tiles scrolling diagonally at 1 px / 8 frames. div_euclid
            // hazard site (spec §2.6): ported as floor division even though
            // the current geometry never goes negative.
            const int Tile = 4;
            int off = unchecked((int)(frame / 8));
            FillWith(s, (x, y) =>
            {
                int cell = (Anim.FloorDiv(x + off, Tile) + Anim.FloorDiv(y + off, Tile)) & 1;
                return cell == 0 ? Black : White;
            });
        }

        /// <summary>
        /// Black flag (spec §5.6), plus the DT/SG service marker.
        /// <paramref name="detail"/> == None reproduces the golden-frozen X
        /// byte-for-byte; the marker arms are additive.
        /// </summary>
        private static void PaintBlackFlag(FrameBuffer s, uint frame, BlackFlagDetail detail)
        {
            // Solid black with a pulsing white "X" across both diagonals.
            const uint Period = 100; // 0.6 Hz at 60 fps
            byte envelope = Anim.Breathe(frame, Period);
            byte m = (byte)(envelope * 130 / 255);
            const int Anti = Width - 1;
            FillWith(s, (x, y) =>
            {
                bool onMain = Math.Abs(x - y) <= 1;
                bool onAnti = Math.Abs(x + y - Anti) <= 1;
                return onMain || onAnti ? new Rgb(m, m, m) : Black;
            });
            if (detail == BlackFlagDetail.None)
            {
                return;
            }
            // Centred two-letter service marker: a black plate (1-px margin
            // around the 15×11 marker row → x 7..23, y 9..21) blanks the X
            // arms behind the letters so DT/SG stay legible at the X's
            // brightness peak, then solid white glyphs. Same centring math
            // as the caution boards: 2 glyphs, gap 1 → 15 px, xLeft 8,
            // yTop 10.
            int plateLeft = TextEngine.CenterRowX(2, 1) - 1;
            int plateTop = TextEngine.CenterRowY() - 1;
            int plateRight = plateLeft + TextEngine.MeasureRow(2, 1) + 1;
            int plateBottom = plateTop + Font7x11.GlyphHeight + 1;
            for (int y = plateTop; y <= plateBottom; y++)
            {
                for (int x = plateLeft; x <= plateRight; x++)
                {
                    s.SetPixel(x, y, Black);
                }
            }
            byte[][] marker = detail == BlackFlagDetail.DriveThrough
                ? new[] { Font7x11.D, Font7x11.T }
                : new[] { Font7x11.S, Font7x11.G };
            TextEngine.DrawCenteredRow(s, marker, 1, White);
        }

        // ---------------------------------------------------------------
        // Penalty boards and accent (pinned by the testdata/frames-plugin/
        // corpus; regenerate via the [windows] leg of `just golden-regen`).
        // ---------------------------------------------------------------

        /// <summary>
        /// Slow-down penalty board: black background, white "SLOW", and an
        /// orange severity digit below it. Severity paces the urgency: the
        /// digit is static at 1, blinks 2 Hz at 2 and 4 Hz at 3 (severities
        /// above 3 clamp to 3). The word row is 4 glyphs, gap 1 → 31 px,
        /// xLeft 0; the digit is centred (xLeft 12).
        /// </summary>
        private static void PaintSlowdown(FrameBuffer s, byte severity, uint frame)
        {
            Fill(s, Black);
            int sev = severity > 3 ? 3 : severity; // dispatch guarantees >= 1
            byte[][] word = { Font7x11.S, Font7x11.L, Font7x11.O, Font7x11.W };
            const int WordGap = 1;
            const int WordTop = 4;
            TextEngine.DrawRow(s, word, WordGap, TextEngine.CenterRowX(word.Length, WordGap), WordTop, White);

            const int DigitTop = 17;
            uint strobeHz = sev == 3 ? 4u : sev == 2 ? 2u : 0u;
            if (strobeHz == 0 || Anim.Strobe60(frame, strobeHz))
            {
                byte[] digit = sev == 3 ? Font7x11.Three : sev == 2 ? Font7x11.Two : Font7x11.One;
                TextEngine.DrawGlyph(s, digit, TextEngine.CenterRowX(1, 0), DigitTop, Orange);
            }
        }

        /// <summary>
        /// Meatball / mandatory-repair board: the real signal is a black
        /// flag with an orange disc, so this paints a filled orange circle
        /// (real-pixel radius ≤ 9.5, centred between pixels like the ready
        /// orb) breathing 180..255 at 1 Hz on black. Deliberately a disc —
        /// the orange flag's rotating quadrants must stay visually distinct.
        /// </summary>
        private static void PaintMeatball(FrameBuffer s, uint frame)
        {
            const uint Period = 60; // 1 Hz at 60 fps
            byte envelope = Anim.Breathe(frame, Period);
            byte m = (byte)(180 + envelope * 75 / 255);
            Rgb disc = Anim.ScaleRgb(Orange, m);
            FillWith(s, (x, y) =>
            {
                // Half-pixel units (spec §5.11 geometry): radius 9.5 px →
                // d² ≤ 19² = 361 in half-pixel units².
                int dx2 = 2 * x - (Width - 1);
                int dy2 = 2 * y - (Height - 1);
                return dx2 * dx2 + dy2 * dy2 <= 361 ? disc : Black;
            });
        }

        /// <summary>
        /// Furled black/white warning accent: a 10×7 diagonally split
        /// black/white tile with a 1-px white frame at top-centre
        /// (x 11..20, y 0..6), blinking at 2 Hz (18/30 duty — off-phase
        /// leaves the base layer untouched). An accent, not a board: the
        /// base keeps telling its story underneath.
        /// </summary>
        private static void PaintFurledAccent(FrameBuffer s, uint frame)
        {
            if (!Anim.Strobe60(frame, 2))
            {
                return;
            }
            const int Left = 11;
            const int Top = 0;
            const int TileW = 10;
            const int TileH = 7;
            for (int y = Top; y < Top + TileH; y++)
            {
                for (int x = Left; x < Left + TileW; x++)
                {
                    bool onFrame = x == Left || x == Left + TileW - 1
                        || y == Top || y == Top + TileH - 1;
                    // Interior 8×5: white in the upper-right triangle —
                    // 5*(x - 12) >= 8*(y - 1) puts the diagonal corner to
                    // corner in integer math.
                    Rgb c = onFrame || 5 * (x - (Left + 1)) >= 8 * (y - (Top + 1))
                        ? White
                        : Black;
                    s.SetPixel(x, y, c);
                }
            }
        }

        // ---------------------------------------------------------------
        // iRacing extension boards + accent (host-only, pinned by the
        // testdata/frames-plugin/ corpus alongside the penalty suite).
        // ---------------------------------------------------------------

        /// <summary>
        /// Start-light gantry: a centred row of five 5×12 bars. Ready — red,
        /// breathing 60..200 at 0.5 Hz (arming); Set — solid full red (hold);
        /// Go — solid green (launch). Ready never reaches full red, so it
        /// stays distinct from Set even at the breathe peak; colour then tells
        /// Set from Go. Black background.
        /// </summary>
        private static void PaintStartLights(FrameBuffer s, StartLights phase, uint frame)
        {
            Fill(s, Black);
            const int Cells = 5;
            const int CellW = 5;
            const int Gap = 1;
            const int Top = 10;
            const int BarH = 12;
            // Five bars + four gaps = 29 px, centred → xLeft 1 (bars at
            // x 1, 7, 13, 19, 25); each spans y 10..21.
            int xLeft = (Width - (Cells * CellW + (Cells - 1) * Gap)) / 2;
            Rgb color;
            switch (phase)
            {
                case StartLights.Go:
                    color = Green;
                    break;
                case StartLights.Set:
                    color = Red;
                    break;
                default: // Ready
                    byte m = (byte)(60 + Anim.Breathe(frame, 120) * 140 / 255);
                    color = Anim.ScaleRgb(Red, m);
                    break;
            }
            for (int cell = 0; cell < Cells; cell++)
            {
                int x0 = xLeft + cell * (CellW + Gap);
                for (int y = Top; y < Top + BarH; y++)
                {
                    for (int x = x0; x < x0 + CellW; x++)
                    {
                        s.SetPixel(x, y, color);
                    }
                }
            }
        }

        /// <summary>
        /// Debris / surface warning board: yellow-and-red diagonal stripes
        /// (the striped surface flag), scrolling toward the top-left at
        /// 1 px / 6 frames. Pure function of the frame counter.
        /// </summary>
        private static void PaintDebris(FrameBuffer s, uint frame)
        {
            const int StripeW = 4;
            int off = unchecked((int)(frame / 6));
            FillWith(s, (x, y) =>
            {
                // FloorDiv keeps the stripe parity correct across the panel;
                // x + y + off never goes negative here, but port it faithfully
                // (docs/effects-spec.md §2.6), like the checkered scroll.
                int stripe = Anim.FloorDiv(x + y + off, StripeW) & 1;
                return stripe == 0 ? Yellow : Red;
            });
        }

        /// <summary>
        /// Incident-limit warning accent: a 1-px red frame around the panel,
        /// blinking at 2 Hz (18/30 duty — off-phase leaves the base
        /// untouched). Distinct from a red flag, which fills the whole panel;
        /// this only tints the edge over whatever the driver is being shown.
        /// </summary>
        private static void PaintIncidentFrame(FrameBuffer s, uint frame)
        {
            if (!Anim.Strobe60(frame, 2))
            {
                return;
            }
            for (int x = 0; x < Width; x++)
            {
                s.SetPixel(x, 0, Red);
                s.SetPixel(x, Height - 1, Red);
            }
            for (int y = 0; y < Height; y++)
            {
                s.SetPixel(0, y, Red);
                s.SetPixel(Width - 1, y, Red);
            }
        }

        // ---------------------------------------------------------------
        // Session idle.
        // ---------------------------------------------------------------

        /// <summary>Race-idle alive marker (spec §5.10).</summary>
        private static void PaintRaceIdle(FrameBuffer s, uint frame)
        {
            Fill(s, Black);
            const byte StaticM = 8;
            const uint Period = 240; // 0.25 Hz at 60 fps
            byte envelope = Anim.Breathe(frame, Period);
            byte pulseM = (byte)(4 + envelope * 10 / 255);
            const int MaxX = Width - 1;
            const int MaxY = Height - 1;
            s.SetPixel(0, 0, StaticM, StaticM, StaticM);
            s.SetPixel(MaxX, 0, StaticM, StaticM, StaticM);
            s.SetPixel(0, MaxY, StaticM, StaticM, StaticM);
            s.SetPixel(MaxX, MaxY, pulseM, pulseM, pulseM);
        }

        /// <summary>Ready orb with 300-frame fallback (spec §5.11).</summary>
        private static void PaintReady(FrameBuffer s, uint frame, uint flagAge)
        {
            const uint OrbDurationFrames = 300; // 5 s at 60 fps
            if (flagAge >= OrbDurationFrames)
            {
                PaintRaceIdle(s, frame);
                return;
            }
            const uint Period = 120; // 0.5 Hz at 60 fps
            byte envelope = Anim.Breathe(frame, Period);
            // Map 0..=255 envelope to brightness 40..=200.
            byte m = (byte)(40 + envelope * 160 / 255);
            // Centre is between pixels at (15.5, 15.5); work in half-pixel
            // units so the ring stays integer-only. r² in half-pixel units²
            // is 64..=144 inclusive (real-pixel radius 4.0..6.0).
            FillWith(s, (x, y) =>
            {
                int dx2 = 2 * x - (Width - 1);
                int dy2 = 2 * y - (Height - 1);
                int dSq = dx2 * dx2 + dy2 * dy2;
                return dSq >= 64 && dSq <= 144 ? new Rgb(0, m, 0) : Black;
            });
        }

        // ---------------------------------------------------------------
        // Caution boards.
        // ---------------------------------------------------------------

        /// <summary>VSC board (spec §5.9).</summary>
        private static void PaintVsc(FrameBuffer s, uint frame) =>
            PaintCautionBoard(s, frame, new[] { Font7x11.V, Font7x11.S, Font7x11.C }, 1);

        /// <summary>Safety-car board (spec §5.9).</summary>
        private static void PaintSafetyCar(FrameBuffer s, uint frame) =>
            PaintCautionBoard(s, frame, new[] { Font7x11.S, Font7x11.C }, 4);

        /// <summary>
        /// Digiflag board: white letters on black, breathing yellow border.
        /// The letter row renders through <see cref="TextEngine.DrawCenteredRow"/>,
        /// whose placement math (same truncating divisions) keeps the boards
        /// byte-identical to the frozen goldens.
        /// </summary>
        private static void PaintCautionBoard(FrameBuffer s, uint frame, byte[][] glyphs, int gap)
        {
            const uint BorderPeriod = 240; // 0.25 Hz at 60 fps
            byte envelope = Anim.Breathe(frame, BorderPeriod);
            // Rust: 200u8.saturating_add(...) — the addend is at most 55 so
            // saturation never fires, but port it faithfully.
            int sum = 200 + envelope * 55 / 255;
            byte m = (byte)(sum > 255 ? 255 : sum);
            Rgb border = Anim.ScaleRgb(Yellow, m);

            FillWith(s, (x, y) =>
            {
                bool onBorder = x < CautionBorder
                    || x >= Width - CautionBorder
                    || y < CautionBorder
                    || y >= Height - CautionBorder;
                return onBorder ? border : Black;
            });

            // Centred letter row (golden-frozen placement).
            TextEngine.DrawCenteredRow(s, glyphs, gap, White);
        }

        // ---------------------------------------------------------------
        // Sector band overlay.
        // ---------------------------------------------------------------

        /// <summary>
        /// Bottom-edge overlay, painted after (over) the base layer (spec §6).
        /// Gap columns 10 and 21 are never written — the base layer shows
        /// through.
        /// </summary>
        private static void PaintSectorBand(FrameBuffer s, SectorSet mask, WaveLevel wave, uint frame)
        {
            // Active sectors pulse 2 Hz (4 Hz on B=2 — B=1 stays at 2 Hz);
            // inactive sectors hold a constant dim so the band is always
            // visible when any sector is set.
            uint strobeHz = wave == WaveLevel.Double ? 4u : 2u;
            bool on = Anim.Strobe60(frame, strobeHz);
            const int YStart = Height - SectorBandHeight;
            for (int idx = 0; idx < 3; idx++)
            {
                int sector = idx + 1;
                bool active = mask.Contains(sector);
                for (int y = YStart; y < Height; y++)
                {
                    for (int x = SegmentLo[idx]; x <= SegmentHi[idx]; x++)
                    {
                        Rgb color;
                        if (active)
                        {
                            color = on
                                ? Anim.ScaleRgb(Yellow, Anim.WaveMult(x, y, frame, 180, 255))
                                : Black;
                        }
                        else
                        {
                            color = SectorDim;
                        }
                        s.SetPixel(x, y, color);
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // Fill helpers.
        // ---------------------------------------------------------------

        private static void Fill(FrameBuffer s, Rgb color)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    s.SetPixel(x, y, color);
                }
            }
        }

        private static void FillWith(FrameBuffer s, Func<int, int, Rgb> f)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    s.SetPixel(x, y, f(x, y));
                }
            }
        }

        /// <summary>Fill every pixel with the cloth-wave-modulated base colour.</summary>
        private static void PaintWithWave(FrameBuffer s, Rgb baseColor, uint frame, byte loMult, byte hiMult)
        {
            FillWith(s, (x, y) => Anim.ScaleRgb(baseColor, Anim.WaveMult(x, y, frame, loMult, hiMult)));
        }
    }
}
