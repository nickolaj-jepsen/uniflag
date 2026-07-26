// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Envelope-tracker tests (docs/flag-grammar.md §4): the flash → attention →
// ambient ladder, escalation/de-escalation re-arm rules, fade-out on clear,
// replacement flashes, and the condition-epoch semantics that keep
// suppressed signals from re-flashing when they re-emerge.

using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
    public class GrammarEnvelopeTests
    {
        private static SignalState S() => SignalState.Default;

        /// <summary>Run <paramref name="frames"/> ticks; returns the last frame's envelopes.</summary>
        private static Envelopes Run(EnvelopeTracker t, in SignalState s, ref uint frame, uint frames)
        {
            Envelopes env = default;
            for (uint i = 0; i < frames; i++)
            {
                env = t.Update(Compositor.Select(s), s, frame);
                frame++;
            }
            return env;
        }

        [Fact]
        public void EntryWalksFlashAttentionAmbient()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Alert;

            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Field.Phase);
            Assert.Equal(0u, env.Field.Age);

            env = Run(t, s, ref frame, 7);
            Assert.Equal(EnvelopePhase.Flash, env.Field.Phase);
            Assert.Equal(7u, env.Field.Age);

            env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Attention, env.Field.Phase);

            env = Run(t, s, ref frame, 291);
            Assert.Equal(EnvelopePhase.Attention, env.Field.Phase);
            Assert.Equal(299u, env.Field.Age);

            env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Ambient, env.Field.Phase);
        }

        [Fact]
        public void TierEscalationRearmsTheWindow()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Ambient;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Field.Phase);

            s.Tier = Tier.Urgent;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Field.Phase);
            Assert.Equal(0u, env.Field.Age);
        }

        [Fact]
        public void DeEscalationDoesNotRearm()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Urgent;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Field.Phase);

            s.Tier = Tier.Alert;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 1).Field.Phase);

            // But a later re-escalation from the stored lower tier re-arms.
            s.Tier = Tier.Urgent;
            Assert.Equal(EnvelopePhase.Flash, Run(t, s, ref frame, 1).Field.Phase);
        }

        [Fact]
        public void BlackDetailGainRearmsLossDoesNot()
        {
            // Bare black settles; escalating it to DQ re-arms the window;
            // dropping back to bare black does not (§4 de-escalation).
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.BlackFlag = true;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Field.Phase);

            s.Disqualified = true;
            Assert.Equal(EnvelopePhase.Flash, Run(t, s, ref frame, 1).Field.Phase);

            Run(t, s, ref frame, 400);
            s.Disqualified = false;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 1).Field.Phase);
        }

        [Fact]
        public void ClearFadesOutThenHides()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Alert;
            Run(t, s, ref frame, 100);

            s.Flag = TrackFlag.None;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.FadeOut, env.Field.Phase);
            Assert.Equal(0u, env.Field.Age);
            Assert.Equal((byte)FieldKind.Yellow, env.Field.PriorKind);

            env = Run(t, s, ref frame, 14);
            Assert.Equal(EnvelopePhase.FadeOut, env.Field.Phase);
            Assert.Equal(14u, env.Field.Age);

            env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Hidden, env.Field.Phase);
        }

        [Fact]
        public void ReplacementFlashesInsteadOfFading()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            Run(t, s, ref frame, 100);

            s.Flag = TrackFlag.Blue;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Field.Phase);
            Assert.Equal(0u, env.Field.Age);
        }

        [Fact]
        public void IncidentDoesNotReflashAfterAFurledWindow()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.IncidentWarning = true;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Frame.Phase);

            s.Furled = true;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Frame.Phase);
            Run(t, s, ref frame, 99);

            s.Furled = false;
            env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Ambient, env.Frame.Phase);
            Assert.Equal(500u, env.Frame.Age);
        }

        [Fact]
        public void SuppressionPreservesTheEpoch()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.SafetyCar = true;
            Run(t, s, ref frame, 100);

            // Red takeover hides the board without clearing its condition.
            s.Flag = TrackFlag.Red;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Hidden, env.Board.Phase);
            Run(t, s, ref frame, 399);

            s.Flag = TrackFlag.None;
            env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Ambient, env.Board.Phase);
            Assert.Equal(500u, env.Board.Age);
        }

        [Fact]
        public void BoardValueChangeRearms()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.CountdownLaps = 10;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Board.Phase);

            s.CountdownLaps = 5;
            Assert.Equal(EnvelopePhase.Flash, Run(t, s, ref frame, 1).Board.Phase);
        }

        [Fact]
        public void GantryPhaseChangeRearms()
        {
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.StartPhase = StartPhase.Ready;
            Run(t, s, ref frame, 200);

            s.StartPhase = StartPhase.Set;
            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Board.Phase);
        }

        [Fact]
        public void ResetDropsAllEnvelopeState()
        {
            // RendererLoop calls Reset when the input leaves Live mode; a
            // signal still present afterwards must replay its onset.
            var t = new EnvelopeTracker();
            uint frame = 0;
            var s = S();
            s.Flag = TrackFlag.Yellow;
            Assert.Equal(EnvelopePhase.Ambient, Run(t, s, ref frame, 400).Field.Phase);

            t.Reset();

            var env = Run(t, s, ref frame, 1);
            Assert.Equal(EnvelopePhase.Flash, env.Field.Phase);
        }
    }
}
