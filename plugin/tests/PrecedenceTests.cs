// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Precedence-ladder tests: unit tests for the dispatch layer
// (plugin/src/Rendering/Precedence.cs) plus the four inline sanity asserts
// ported from render/tests/effects.rs (disconnected blank, red-over-caution,
// sector band suppressed under red, sector band present without red).

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>Unit tests for <see cref="Precedence"/> — dispatch only, no pixels.</summary>
    public class PrecedenceDispatchTests
    {
        private static RenderState With(
            Flag flag = Flag.None,
            WaveLevel wave = WaveLevel.None,
            Session session = Session.Unknown,
            Caution caution = Caution.None,
            byte sectorBits = 0)
        {
            var state = RenderState.Default;
            state.Flag = flag;
            state.Wave = wave;
            state.Session = session;
            state.Caution = caution;
            state.Sectors = SectorSet.FromBits(sectorBits);
            return state;
        }

        [Fact]
        public void DisconnectedBeatsEverything()
        {
            var state = With(flag: Flag.Red, caution: Caution.SafetyCar, sectorBits: 0b111);
            Assert.Equal(RenderLayer.Disconnected, Precedence.Select(state, connected: false));
        }

        [Fact]
        public void RedBeatsCaution()
        {
            Assert.Equal(
                RenderLayer.RedFlag,
                Precedence.Select(With(flag: Flag.Red, caution: Caution.SafetyCar), connected: true));
            Assert.Equal(
                RenderLayer.RedFlag,
                Precedence.Select(With(flag: Flag.Red, caution: Caution.VirtualSafetyCar), connected: true));
        }

        [Theory]
        [InlineData(Flag.Yellow)]
        [InlineData(Flag.Blue)]
        [InlineData(Flag.Green)]
        [InlineData(Flag.White)]
        [InlineData(Flag.Black)]
        [InlineData(Flag.Orange)]
        [InlineData(Flag.Checkered)]
        [InlineData(Flag.None)]
        public void CautionBeatsEveryFlagExceptRed(Flag flag)
        {
            Assert.Equal(
                RenderLayer.VscBoard,
                Precedence.Select(With(flag: flag, caution: Caution.VirtualSafetyCar), connected: true));
            Assert.Equal(
                RenderLayer.SafetyCarBoard,
                Precedence.Select(With(flag: flag, caution: Caution.SafetyCar), connected: true));
        }

        [Theory]
        [InlineData(Flag.Yellow, RenderLayer.YellowFlag)]
        [InlineData(Flag.Blue, RenderLayer.BlueFlag)]
        [InlineData(Flag.Green, RenderLayer.GreenFlag)]
        [InlineData(Flag.White, RenderLayer.WhiteFlag)]
        [InlineData(Flag.Black, RenderLayer.BlackFlag)]
        [InlineData(Flag.Orange, RenderLayer.OrangeFlag)]
        [InlineData(Flag.Checkered, RenderLayer.CheckeredFlag)]
        [InlineData(Flag.Red, RenderLayer.RedFlag)]
        public void PerFlagBaseLayersDispatchDirectly(Flag flag, RenderLayer expected)
        {
            Assert.Equal(expected, Precedence.Select(With(flag: flag), connected: true));
        }

        [Theory]
        [InlineData(Session.Racing, RenderLayer.RaceIdle)]
        [InlineData(Session.Paused, RenderLayer.RaceIdle)]
        [InlineData(Session.PreRace, RenderLayer.ReadyOrb)]
        [InlineData(Session.PostRace, RenderLayer.ReadyOrb)]
        [InlineData(Session.Replay, RenderLayer.ReadyOrb)]
        [InlineData(Session.Unknown, RenderLayer.ReadyOrb)]
        public void NoFlagFallsThroughToSessionIdle(Session session, RenderLayer expected)
        {
            Assert.Equal(expected, Precedence.Select(With(session: session), connected: true));
        }

        [Fact]
        public void SectorBandVisibilityRules()
        {
            // Visible: sectors set, connected, not red — over any base layer.
            Assert.True(Precedence.SectorBandVisible(With(sectorBits: 0b010), connected: true));
            Assert.True(Precedence.SectorBandVisible(
                With(flag: Flag.Yellow, sectorBits: 0b010), connected: true));
            // The band does overlay caution boards.
            Assert.True(Precedence.SectorBandVisible(
                With(caution: Caution.SafetyCar, sectorBits: 0b010), connected: true));
            // Suppressed: red flag, empty mask, or disconnected.
            Assert.False(Precedence.SectorBandVisible(
                With(flag: Flag.Red, sectorBits: 0b111), connected: true));
            Assert.False(Precedence.SectorBandVisible(With(), connected: true));
            Assert.False(Precedence.SectorBandVisible(With(sectorBits: 0b010), connected: false));
        }
    }

    /// <summary>
    /// The four inline sanity asserts from <c>render/tests/effects.rs:20-88</c>,
    /// ported against the C# renderer's pixels.
    /// </summary>
    public class PrecedencePaintTests
    {
        private static FrameBuffer Paint(RenderState state, uint frame, uint flagAge, bool connected)
        {
            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, state, frame, flagAge, connected);
            return frameBuffer;
        }

        private static int CountLit(FrameBuffer frameBuffer)
        {
            int lit = 0;
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    Rgb px = frameBuffer.GetPixel(x, y);
                    if (px.R > 0 || px.G > 0 || px.B > 0)
                    {
                        lit++;
                    }
                }
            }
            return lit;
        }

        private static int CountRedish(FrameBuffer frameBuffer)
        {
            int count = 0;
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    Rgb px = frameBuffer.GetPixel(x, y);
                    if (px.R > px.G && px.R > px.B && px.R > 0)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        [Fact]
        public void DisconnectedBlanksPanel()
        {
            FrameBuffer frameBuffer = Paint(RenderState.Default, 0, 0, connected: false);
            Assert.True(
                CountLit(frameBuffer) == 0,
                "panel should be entirely black when host is silent");
        }

        [Fact]
        public void RedOverridesCaution()
        {
            var state = RenderState.Default;
            state.Flag = Flag.Red;
            state.Caution = Caution.SafetyCar;
            // After the 4-frame onset, red is solid red — most pixels should
            // have a dominant red channel. The caution-board border (which
            // would dominate if precedence were wrong) is yellow.
            FrameBuffer frameBuffer = Paint(state, 100, 100, connected: true);
            int redish = CountRedish(frameBuffer);
            Assert.True(
                redish > 900,
                $"red flag should fill the panel after onset (got {redish} red-dominant pixels)");
        }

        [Fact]
        public void SectorBandSuppressedUnderRed()
        {
            var state = RenderState.Default;
            state.Flag = Flag.Red;
            state.Sectors = SectorSet.FromBits(0b111);
            FrameBuffer frameBuffer = Paint(state, 100, 100, connected: true);
            // Bottom two rows should be red (red wins, sector overlay
            // suppressed). SECTOR_DIM = (40, 30, 0) is yellow-ish — assert no
            // yellow-dominant pixels in the band.
            for (int y = FrameBuffer.Height - 2; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    Rgb px = frameBuffer.GetPixel(x, y);
                    Assert.True(
                        px.R > px.G && px.R > px.B,
                        $"pixel ({x},{y}) under red+sector mask should still be red, got {px}");
                }
            }
        }

        [Fact]
        public void SectorBandPresentWithoutRed()
        {
            var state = RenderState.Default;
            state.Session = Session.Racing;
            state.Sectors = SectorSet.FromBits(0b010); // S2 only
            // Frame 0: strobe is in on-phase; S2 segment should be lit yellow.
            FrameBuffer frameBuffer = Paint(state, 0, 0, connected: true);
            Rgb px = frameBuffer.GetPixel(15, FrameBuffer.Height - 1);
            Assert.True(px.R > 0 && px.G > 0 && px.B == 0, $"S2 segment should be yellow-ish, got {px}");
        }
    }
}
