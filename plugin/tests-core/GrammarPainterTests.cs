// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Painter tests for the Grammar renderer (docs/flag-grammar.md §4–§8):
// envelope phases (flash whiteout, tier modulation, fade dimming), field
// geometry (X, disc, sweeps, stripes), board chrome, frame rings, the
// sector strip, and the Watchline idles. These pin structure and dispatch at
// specific pixels; GrammarSmokeTests covers whole-scenario replay. Nothing
// pins the frame wholesale — see docs/flag-grammar.md §11.

using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;
using Xunit;
using Session = Uniflag.Rendering.Session;

namespace Uniflag.Tests
{
    public class GrammarPainterTests
    {
        private static SignalState S() => SignalState.Default;

        private static FrameBuffer Render(in SignalState state, in Envelopes env, uint frame)
        {
            var s = new FrameBuffer();
            var comp = Compositor.Select(state, true);
            Painter.Paint(s, comp, env, state, frame);
            return s;
        }

        private static Envelopes FieldEnv(EnvelopePhase phase, uint age) =>
            new Envelopes { Field = new SlotEnvelope { Phase = phase, Age = age } };

        [Fact]
        public void FlashOpensSolidWhite()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            var buf = Render(s, FieldEnv(EnvelopePhase.Flash, 0), 0);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    Assert.Equal(new Rgb(255, 255, 255), buf.GetPixel(x, y));
                }
            }
        }

        [Fact]
        public void YellowAmbientIsTheClothWave()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            var buf = Render(s, FieldEnv(EnvelopePhase.Ambient, 400), 123);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    Assert.Equal(
                        Anim.ScaleRgb(Palette.Yellow, Anim.WaveMult(x, y, 123, 150, 255)),
                        buf.GetPixel(x, y));
                }
            }
        }

        [Fact]
        public void UrgentOffPhaseCutsToBlack()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Urgent;
            Assert.False(Anim.Strobe60(13, 4));
            var buf = Render(s, FieldEnv(EnvelopePhase.Attention, 100), 13);
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(16, 16));
        }

        [Fact]
        public void AlertOffPhaseDimsInsteadOfCutting()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Alert;
            Assert.False(Anim.Strobe60(20, 2));
            var buf = Render(s, FieldEnv(EnvelopePhase.Attention, 100), 20);
            var expected = Anim.ScaleRgb(
                Anim.ScaleRgb(Palette.Yellow, Anim.WaveMult(16, 16, 20, 150, 255)), 90);
            Assert.Equal(expected, buf.GetPixel(16, 16));
            Assert.NotEqual(new Rgb(0, 0, 0), buf.GetPixel(16, 16));
        }

        [Fact]
        public void BlackFieldDrawsTheBreathingX()
        {
            var s = S();
            s.BlackFlag = true;
            // Frame 60: Breathe(60, 240) peaks at 255 -> X brightness 180.
            var buf = Render(s, FieldEnv(EnvelopePhase.Ambient, 400), 60);
            Assert.Equal(new Rgb(180, 180, 180), buf.GetPixel(0, 0));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(5, 0));
        }

        [Fact]
        public void DisqualifiedXIsSteadyEvenOffPhase()
        {
            var s = S();
            s.Disqualified = true;
            Assert.False(Anim.Strobe60(20, 2));
            var buf = Render(s, FieldEnv(EnvelopePhase.Attention, 100), 20);
            Assert.Equal(new Rgb(200, 200, 200), buf.GetPixel(0, 0));
        }

        [Fact]
        public void MeatballFieldIsTheOrangeDisc()
        {
            var s = S();
            s.Meatball = true;
            var buf = Render(s, FieldEnv(EnvelopePhase.Ambient, 400), 60);
            Assert.Equal(Palette.Orange, buf.GetPixel(15, 15));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(0, 0));
        }

        [Fact]
        public void GreenOnsetStartsSolidThenSweeps()
        {
            var s = S();
            s.Flag = TrackFlag.Green;
            var buf = Render(s, FieldEnv(EnvelopePhase.Flash, 0), 0);
            Assert.Equal(Palette.Green, buf.GetPixel(0, 0));
            Assert.Equal(Palette.Green, buf.GetPixel(31, 31));

            // Age 15: band centre at x = 16.
            buf = Render(s, FieldEnv(EnvelopePhase.Attention, 15), 15);
            Assert.Equal(Palette.GreenSweepBand, buf.GetPixel(16, 5));
            Assert.Equal(Palette.Green, buf.GetPixel(0, 5));
        }

        [Fact]
        public void BlueSweepBandRidesTheClothWave()
        {
            var s = S();
            s.Flag = TrackFlag.Blue;
            s.Tier = Tier.Alert;
            // Frame 100, step 2: band at x = 18..21.
            var buf = Render(s, FieldEnv(EnvelopePhase.Attention, 100), 100);
            Assert.Equal(Palette.Blue, buf.GetPixel(18, 0));
            Assert.Equal(
                Anim.ScaleRgb(Palette.Blue, Anim.WaveMult(10, 0, 100, 150, 255)),
                buf.GetPixel(10, 0));
        }

        [Fact]
        public void DebrisFieldIsStripes()
        {
            var s = S();
            s.Flag = TrackFlag.Debris;
            var buf = Render(s, FieldEnv(EnvelopePhase.Ambient, 400), 0);
            Assert.Equal(Palette.Yellow, buf.GetPixel(0, 0));
            Assert.Equal(Palette.Red, buf.GetPixel(4, 0));
            Assert.Equal(Palette.Red, buf.GetPixel(0, 4));
        }

        [Fact]
        public void BoardChromeSitsOverTheField()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.SafetyCar = true;
            var env = new Envelopes
            {
                Field = new SlotEnvelope { Phase = EnvelopePhase.Ambient, Age = 400 },
                Board = new SlotEnvelope { Phase = EnvelopePhase.Ambient, Age = 400 },
            };
            var buf = Render(s, env, 7);
            // SC board: 21x19 box centred at x 5..25, y 6..24.
            Assert.Equal(new Rgb(255, 255, 255), buf.GetPixel(5, 6));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(6, 7));
            Assert.Equal(
                Anim.ScaleRgb(Palette.Yellow, Anim.WaveMult(0, 0, 7, 150, 255)),
                buf.GetPixel(0, 0));
        }

        [Fact]
        public void GantrySetLightsAllFiveRed()
        {
            var s = S();
            s.StartPhase = StartPhase.Set;
            var env = new Envelopes
            {
                Board = new SlotEnvelope { Phase = EnvelopePhase.Ambient, Age = 400 },
            };
            var buf = Render(s, env, 0);
            // Gantry box 30x12 at x 1..30, y 10..21; lights start at (4, 14).
            Assert.Equal(Palette.Red, buf.GetPixel(4, 14));
            Assert.Equal(Palette.Red, buf.GetPixel(14, 14));
            Assert.Equal(Palette.Red, buf.GetPixel(19, 14));
            Assert.Equal(Palette.Red, buf.GetPixel(24, 14));
        }

        [Fact]
        public void GantryGoLeavesEverySocketDark()
        {
            var s = S();
            s.StartPhase = StartPhase.Go;
            var env = new Envelopes
            {
                Board = new SlotEnvelope { Phase = EnvelopePhase.Ambient, Age = 400 },
            };
            var buf = Render(s, env, 0);
            Assert.Equal(Palette.GantrySocket, buf.GetPixel(4, 14));
            Assert.Equal(Palette.GantrySocket, buf.GetPixel(24, 14));
        }

        [Fact]
        public void IncidentRingSettlesDim()
        {
            var s = S();
            s.IncidentWarning = true;
            var env = new Envelopes
            {
                Frame = new SlotEnvelope { Phase = EnvelopePhase.Ambient, Age = 400 },
            };
            var buf = Render(s, env, 0);
            Assert.Equal(Anim.ScaleRgb(Palette.Red, 60), buf.GetPixel(0, 0));
            Assert.Equal(Anim.ScaleRgb(Palette.Red, 60), buf.GetPixel(31, 15));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(1, 1));
        }

        [Fact]
        public void FurledRingIsDashes()
        {
            var s = S();
            s.Furled = true;
            var env = new Envelopes
            {
                Frame = new SlotEnvelope { Phase = EnvelopePhase.Attention, Age = 100 },
            };
            Assert.True(Anim.Strobe60(0, 2));
            var buf = Render(s, env, 0);
            Assert.Equal(new Rgb(255, 255, 255), buf.GetPixel(0, 0));
            Assert.Equal(new Rgb(255, 255, 255), buf.GetPixel(3, 0));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(4, 0));
        }

        [Fact]
        public void FadeOutScalesThePriorField()
        {
            var s = S();
            var env = new Envelopes
            {
                Field = new SlotEnvelope
                {
                    Phase = EnvelopePhase.FadeOut,
                    Age = 14,
                    PriorKind = (byte)FieldKind.Yellow,
                    PriorTier = Tier.Ambient,
                },
            };
            var buf = Render(s, env, 50);
            Assert.Equal(
                Anim.ScaleRgb(Anim.ScaleRgb(Palette.Yellow, Anim.WaveMult(16, 16, 50, 150, 255)), 17),
                buf.GetPixel(16, 16));
        }

        [Fact]
        public void RaceIdleIsFourStaticTicks()
        {
            var s = S();
            s.Session = Session.Racing;
            var buf = Render(s, default, 999);
            Assert.Equal(new Rgb(8, 8, 8), buf.GetPixel(1, 30));
            Assert.Equal(new Rgb(4, 4, 4), buf.GetPixel(2, 30));
            Assert.Equal(new Rgb(4, 4, 4), buf.GetPixel(29, 30));
            Assert.Equal(new Rgb(8, 8, 8), buf.GetPixel(30, 30));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(16, 16));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(1, 31));
        }

        [Fact]
        public void SessionIdleFlankersBreatheAntiPhase()
        {
            var s = S();
            // Frame 60: left envelope peaks (m = 20), right troughs (m = 8).
            var buf = Render(s, default, 60);
            Assert.Equal(Anim.ScaleRgb(Palette.Violet, 20), buf.GetPixel(8, 30));
            Assert.Equal(Anim.ScaleRgb(Palette.Violet, 8), buf.GetPixel(23, 30));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(16, 16));
        }

        [Fact]
        public void ConnectedIdleIsTheTealBeacon()
        {
            var buf = new FrameBuffer();
            Idles.PaintConnectedIdle(buf, 60);
            Assert.Equal(Anim.ScaleRgb(Palette.Teal, 24), buf.GetPixel(15, 30));
            Assert.Equal(Anim.ScaleRgb(Palette.Teal, (24 * 3) >> 3), buf.GetPixel(14, 30));
            Assert.Equal(Anim.ScaleRgb(Palette.Teal, 24 >> 2), buf.GetPixel(15, 29));
            Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(0, 0));
        }

        [Fact]
        public void DisconnectedPaintsBlank()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            var buf = new FrameBuffer();
            Painter.Paint(buf, Compositor.Select(s, false), default, s, 0);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    Assert.Equal(new Rgb(0, 0, 0), buf.GetPixel(x, y));
                }
            }
        }
    }
}
