// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Penalty-suite unit tests (M10 step 2/3): the extended precedence ladder,
// the default-means-none invariant (every new code path unreachable when
// the penalty fields hold their defaults — the 40 ported-parity goldens
// prove the byte level; these tests prove the dispatch level), the furled
// accent visibility rules, and painter-level sanity the frames-plugin
// corpus then pins byte-exactly.

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class PenaltyPrecedenceTests
    {
        private static RenderState Penalty(
            Flag flag = Flag.None,
            Caution caution = Caution.None,
            byte slowdown = 0,
            bool meatball = false,
            BlackFlagDetail blackDetail = BlackFlagDetail.None,
            bool furled = false)
        {
            RenderState state = RenderState.Default;
            state.Flag = flag;
            state.Session = Session.Racing;
            state.Caution = caution;
            state.Slowdown = slowdown;
            state.Meatball = meatball;
            state.BlackDetail = blackDetail;
            state.Furled = furled;
            return state;
        }

        [Fact]
        public void DefaultPenaltyStateNeverReachesThePenaltyLayers()
        {
            // Sweep the pre-M10 vocabulary: with default penalty fields the
            // dispatch must never pick a penalty layer, keeping every new
            // code path unreachable for existing states.
            foreach (Flag flag in (Flag[])System.Enum.GetValues(typeof(Flag)))
            {
                foreach (Caution caution in (Caution[])System.Enum.GetValues(typeof(Caution)))
                {
                    RenderLayer layer = Precedence.Select(
                        Penalty(flag: flag, caution: caution), connected: true);
                    Assert.NotEqual(RenderLayer.SlowdownBoard, layer);
                    Assert.NotEqual(RenderLayer.MeatballBoard, layer);
                }
            }
        }

        [Fact]
        public void SlowdownOutranksMeatballAndEveryFlagBase()
        {
            Assert.Equal(
                RenderLayer.SlowdownBoard,
                Precedence.Select(Penalty(slowdown: 1, meatball: true), connected: true));
            foreach (Flag flag in new[] { Flag.Yellow, Flag.Black, Flag.Orange, Flag.Checkered })
            {
                Assert.Equal(
                    RenderLayer.SlowdownBoard,
                    Precedence.Select(Penalty(flag: flag, slowdown: 3), connected: true));
            }
        }

        [Fact]
        public void MeatballOutranksEveryFlagBase()
        {
            // Including orange — the unified layer maps iRacing's repair bit
            // to Flag_Orange, so meatball + orange is the common real pair
            // and the meatball board must win.
            foreach (Flag flag in new[] { Flag.Yellow, Flag.Black, Flag.Orange, Flag.Blue })
            {
                Assert.Equal(
                    RenderLayer.MeatballBoard,
                    Precedence.Select(Penalty(flag: flag, meatball: true), connected: true));
            }
        }

        [Fact]
        public void CautionBoardsOutrankPenaltyBoards()
        {
            Assert.Equal(
                RenderLayer.SafetyCarBoard,
                Precedence.Select(
                    Penalty(caution: Caution.SafetyCar, slowdown: 3, meatball: true),
                    connected: true));
            Assert.Equal(
                RenderLayer.VscBoard,
                Precedence.Select(
                    Penalty(caution: Caution.VirtualSafetyCar, slowdown: 3, meatball: true),
                    connected: true));
        }

        [Fact]
        public void RedAndDisconnectedOutrankPenalties()
        {
            RenderState state = Penalty(flag: Flag.Red, slowdown: 3, meatball: true, furled: true);
            Assert.Equal(RenderLayer.RedFlag, Precedence.Select(state, connected: true));
            Assert.Equal(RenderLayer.Disconnected, Precedence.Select(state, connected: false));
        }

        [Fact]
        public void BlackDetailIsAVariantNotALayer()
        {
            // DT/SG stay on the black-flag base layer; the detail only
            // changes what PaintBlackFlag draws.
            Assert.Equal(
                RenderLayer.BlackFlag,
                Precedence.Select(
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.DriveThrough),
                    connected: true));
            Assert.Equal(
                RenderLayer.BlackFlag,
                Precedence.Select(
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.StopAndGo),
                    connected: true));
        }

        [Fact]
        public void FurledAccentVisibilityMirrorsTheSectorBandRules()
        {
            // Visible over bases, boards and idle; suppressed under red and
            // when disconnected; never visible unless set.
            Assert.True(Precedence.FurledAccentVisible(Penalty(furled: true), connected: true));
            Assert.True(Precedence.FurledAccentVisible(
                Penalty(flag: Flag.Yellow, furled: true), connected: true));
            Assert.True(Precedence.FurledAccentVisible(
                Penalty(caution: Caution.SafetyCar, furled: true), connected: true));
            Assert.False(Precedence.FurledAccentVisible(
                Penalty(flag: Flag.Red, furled: true), connected: true));
            Assert.False(Precedence.FurledAccentVisible(Penalty(furled: true), connected: false));
            Assert.False(Precedence.FurledAccentVisible(Penalty(), connected: true));
        }
    }

    public class PenaltyPaintTests
    {
        private static FrameBuffer Paint(RenderState state, uint frame)
        {
            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, state, frame, 100, connected: true);
            return frameBuffer;
        }

        private static RenderState Slowdown(byte severity)
        {
            RenderState state = RenderState.Default;
            state.Session = Session.Racing;
            state.Slowdown = severity;
            return state;
        }

        [Fact]
        public void SlowdownSeverityAboveThreeClampsToThree()
        {
            // Severity is a byte with three defined grades; anything larger
            // must render exactly like 3, never index off the digit table.
            byte[] atThree = Paint(Slowdown(3), 5).Pixels;
            byte[] clamped = Paint(Slowdown(200), 5).Pixels;
            Assert.Equal(atThree, clamped);
        }

        [Fact]
        public void SlowdownSeverityOneIsStaticAcrossTheStrobePeriod()
        {
            // Severity 1 has no blink: any two frames deep in the 2 Hz /
            // 4 Hz windows must be identical.
            Assert.Equal(Paint(Slowdown(1), 10).Pixels, Paint(Slowdown(1), 20).Pixels);
        }

        [Fact]
        public void SlowdownDigitBlanksInTheOffPhaseButTheWordStays()
        {
            FrameBuffer on = Paint(Slowdown(2), 10);
            FrameBuffer off = Paint(Slowdown(2), 20);
            // The digit occupies rows 17..27 around x 12..18: lit orange in
            // the on-phase (digit 2's top bar at y 17), dark in the off-phase.
            Assert.Equal(new Rgb(255, 90, 0), on.GetPixel(14, 17));
            Assert.Equal(new Rgb(0, 0, 0), off.GetPixel(14, 17));
            // "SLOW" is static white in both phases — S column at (1, 4)
            // (glyph S row 0 bit pattern .#####. from x 0).
            Assert.Equal(new Rgb(255, 255, 255), on.GetPixel(1, 4));
            Assert.Equal(new Rgb(255, 255, 255), off.GetPixel(1, 4));
        }

        [Fact]
        public void MeatballIsADiscNotQuadrants()
        {
            RenderState state = RenderState.Default;
            state.Meatball = true;
            // Frame 15 = breathe peak → full ORANGE disc.
            FrameBuffer meatball = Paint(state, 15);
            // Centre lit orange, all four corners black — the orange-flag
            // quadrant effect always lights two full quadrants, so corners
            // distinguish the two at a glance.
            Assert.Equal(new Rgb(255, 90, 0), meatball.GetPixel(15, 15));
            Assert.Equal(new Rgb(0, 0, 0), meatball.GetPixel(0, 0));
            Assert.Equal(new Rgb(0, 0, 0), meatball.GetPixel(31, 0));
            Assert.Equal(new Rgb(0, 0, 0), meatball.GetPixel(0, 31));
            Assert.Equal(new Rgb(0, 0, 0), meatball.GetPixel(31, 31));
        }

        [Fact]
        public void BlackDetailNoneRendersThePlainX()
        {
            // The golden corpus proves this byte-for-byte; assert the
            // centre-marker pixels stay X-grey here so a future edit that
            // accidentally stamps a marker for None is caught at unit level.
            RenderState plain = RenderState.Default;
            plain.Flag = Flag.Black;
            FrameBuffer frameBuffer = Paint(plain, 25);
            // (19, 15) is the T stem when the DT marker is stamped (T at
            // xLeft 16, stem columns x 18..20) and sits off both X
            // diagonals (main diagonal at y 15 covers x 14..16, the
            // anti-diagonal x 15..17) — so it must be pure black for
            // detail None and pure white for DriveThrough.
            Assert.Equal(new Rgb(0, 0, 0), frameBuffer.GetPixel(19, 15));

            RenderState dt = plain;
            dt.BlackDetail = BlackFlagDetail.DriveThrough;
            Assert.Equal(new Rgb(255, 255, 255), Paint(dt, 25).GetPixel(19, 15));
        }

        [Fact]
        public void FurledOffPhaseLeavesTheBaseUntouched()
        {
            RenderState furled = RenderState.Default;
            furled.Session = Session.Racing;
            furled.Furled = true;
            RenderState bare = RenderState.Default;
            bare.Session = Session.Racing;
            // Frame 20 is the 2 Hz off-phase: accent invisible, frame must
            // equal the plain base byte-for-byte.
            Assert.Equal(Paint(bare, 20).Pixels, Paint(furled, 20).Pixels);
            // Frame 10 is the on-phase: the tile's white frame appears.
            Assert.Equal(new Rgb(255, 255, 255), Paint(furled, 10).GetPixel(11, 0));
        }

        [Fact]
        public void FurledTileSplitsBlackOverWhiteDiagonally()
        {
            RenderState state = RenderState.Default;
            state.Session = Session.Racing;
            state.Furled = true;
            FrameBuffer frameBuffer = Paint(state, 10);
            // Interior x 12..19, y 1..5: upper-right triangle white,
            // lower-left black (5*(x-12) >= 8*(y-1)).
            Assert.Equal(new Rgb(255, 255, 255), frameBuffer.GetPixel(19, 1));
            Assert.Equal(new Rgb(0, 0, 0), frameBuffer.GetPixel(12, 5));
        }
    }
}
