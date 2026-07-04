// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Dispatch + painter sanity for the iRacing extension effects (start-light
// gantry, debris board, incident-limit warning frame). The frames-plugin
// corpus pins the pixels byte-exactly; these tests prove the dispatch level:
// the new layers/accent stay unreachable at default state (so the frozen
// corpora are safe), only dispatch in the idle arm, and the painters produce
// the expected colours at their pinned frames.

using System;
using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class IRacingExtrasPrecedenceTests
    {
        [Fact]
        public void DefaultExtrasNeverReachTheirLayersOrAccent()
        {
            // Every flag/caution combination with default StartLights/Debris/
            // IncidentWarning must dispatch exactly as before — the two new
            // base layers and the accent stay unreachable, keeping every
            // existing (state, frame) tuple byte-identical.
            foreach (Flag flag in (Flag[])Enum.GetValues(typeof(Flag)))
            {
                foreach (Caution caution in (Caution[])Enum.GetValues(typeof(Caution)))
                {
                    RenderState s = RenderState.Default;
                    s.Flag = flag;
                    s.Caution = caution;
                    s.Session = Session.Racing;
                    RenderLayer layer = Precedence.Select(s, connected: true);
                    Assert.NotEqual(RenderLayer.StartLightsBoard, layer);
                    Assert.NotEqual(RenderLayer.DebrisBoard, layer);
                    Assert.False(Precedence.IncidentWarningVisible(s, connected: true));
                }
            }
        }

        [Fact]
        public void StartLightsAndDebrisOnlyDispatchInTheIdleArm()
        {
            RenderState start = RenderState.Default;
            start.Session = Session.Racing;
            start.StartLights = StartLights.Set;
            Assert.Equal(RenderLayer.StartLightsBoard, Precedence.Select(start, connected: true));

            RenderState debris = RenderState.Default;
            debris.Session = Session.Racing;
            debris.Debris = true;
            Assert.Equal(RenderLayer.DebrisBoard, Precedence.Select(debris, connected: true));

            // A caution board (or any flag) outranks both.
            debris.Caution = Caution.SafetyCar;
            Assert.Equal(RenderLayer.SafetyCarBoard, Precedence.Select(debris, connected: true));
            start.Flag = Flag.Blue;
            Assert.Equal(RenderLayer.BlueFlag, Precedence.Select(start, connected: true));
        }

        [Fact]
        public void IncidentWarningVisibilityMirrorsTheFurledAccentRules()
        {
            RenderState warn = RenderState.Default;
            warn.Session = Session.Racing;
            warn.IncidentWarning = true;
            Assert.True(Precedence.IncidentWarningVisible(warn, connected: true));

            // Rides over any non-red base.
            RenderState overYellow = warn;
            overYellow.Flag = Flag.Yellow;
            Assert.True(Precedence.IncidentWarningVisible(overYellow, connected: true));

            // Suppressed under red, disconnected, or when unset.
            RenderState underRed = warn;
            underRed.Flag = Flag.Red;
            Assert.False(Precedence.IncidentWarningVisible(underRed, connected: true));
            Assert.False(Precedence.IncidentWarningVisible(warn, connected: false));
            Assert.False(Precedence.IncidentWarningVisible(RenderState.Default, connected: true));
        }
    }

    public class IRacingExtrasPaintTests
    {
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Green = new Rgb(0, 220, 0);
        private static readonly Rgb Yellow = new Rgb(255, 220, 0);
        private static readonly Rgb Black = new Rgb(0, 0, 0);

        private static FrameBuffer Paint(RenderState state, uint frame)
        {
            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, state, frame, 100, connected: true);
            return frameBuffer;
        }

        private static RenderState StartState(StartLights phase)
        {
            RenderState state = RenderState.Default;
            state.Session = Session.Racing;
            state.StartLights = phase;
            return state;
        }

        [Fact]
        public void StartLightsSetIsSolidRedBarsAndGoIsGreen()
        {
            // Five bars at x 1,7,13,19,25 (each 5 wide), spanning y 10..21.
            FrameBuffer set = Paint(StartState(StartLights.Set), 0);
            Assert.Equal(Red, set.GetPixel(3, 15));   // first bar centre
            Assert.Equal(Black, set.GetPixel(6, 15)); // gap column
            Assert.Equal(Black, set.GetPixel(3, 5));  // above the bar row

            FrameBuffer go = Paint(StartState(StartLights.Go), 0);
            Assert.Equal(Green, go.GetPixel(3, 15));
        }

        [Fact]
        public void StartLightsReadyBreathesBelowFullRed()
        {
            // Ready scales red 60..200 — never full 255, so it stays distinct
            // from Set at every frame. Frame 30 is the breathe peak (m 200).
            Rgb px = Paint(StartState(StartLights.Ready), 30).GetPixel(3, 15);
            Assert.True(px.R > 0 && px.G == 0 && px.B == 0, $"ready bar should be pure red-ish, got {px}");
            Assert.True(px.R < 255, $"ready red must stay below full (got {px.R}) to differ from Set");
        }

        [Fact]
        public void DebrisBoardIsAllYellowAndRedStripes()
        {
            RenderState state = RenderState.Default;
            state.Session = Session.Racing;
            state.Debris = true;
            FrameBuffer fb = Paint(state, 0); // frame 0 → scroll offset 0

            // stripe = floor((x + y) / 4) & 1 at offset 0.
            Assert.Equal(Yellow, fb.GetPixel(0, 0)); // stripe 0
            Assert.Equal(Red, fb.GetPixel(4, 0));    // stripe 1

            // The board is entirely yellow-or-red — no black, no other colour.
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    Rgb px = fb.GetPixel(x, y);
                    Assert.True(
                        px.Equals(Yellow) || px.Equals(Red),
                        $"debris pixel ({x},{y}) should be yellow or red, got {px}");
                }
            }
        }

        [Fact]
        public void IncidentFrameTintsTheEdgeOnPhaseAndVanishesOffPhase()
        {
            RenderState warn = RenderState.Default;
            warn.Session = Session.Racing;
            warn.IncidentWarning = true;
            RenderState bare = RenderState.Default;
            bare.Session = Session.Racing;

            // 2 Hz blink: frame 10 on-phase (< 18), frame 20 off-phase.
            FrameBuffer on = Paint(warn, 10);
            Assert.Equal(Red, on.GetPixel(0, 0));   // corner
            Assert.Equal(Red, on.GetPixel(15, 0));  // top edge
            Assert.Equal(Red, on.GetPixel(0, 15));  // left edge
            Assert.Equal(Red, on.GetPixel(31, 31)); // opposite corner

            // Off-phase leaves the base untouched, byte-for-byte.
            Assert.Equal(Paint(bare, 20).Pixels, Paint(warn, 20).Pixels);
        }

        [Fact]
        public void IncidentFrameRidesOverAFlagBase()
        {
            RenderState warn = RenderState.Default;
            warn.Session = Session.Racing;
            warn.Flag = Flag.Yellow;
            warn.IncidentWarning = true;
            // On-phase: the edge is red even though the base layer is yellow.
            Assert.Equal(Red, Paint(warn, 10).GetPixel(0, 0));
        }
    }
}
