// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Slot-selection tests for the Grammar compositor (docs/flag-grammar.md §5):
// takeovers, the demotion rule, caution-forced yellow, within-slot
// precedence, and strip/frame suppression.

using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
    public class GrammarCompositorTests
    {
        private static SignalState S() => SignalState.Default;

        [Fact]
        public void DisconnectedIsBlank()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            var c = Compositor.Select(s, connected: false);
            Assert.False(c.Connected);
            Assert.Equal(FieldKind.None, c.Field);
            Assert.Equal(BoardKind.None, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
        }

        [Fact]
        public void RedIsATotalTakeover()
        {
            var s = S();
            s.Flag = TrackFlag.Red;
            s.SafetyCar = true;
            s.Furled = true;
            s.IncidentWarning = true;
            s.StartPhase = StartPhase.Ready;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Red, c.Field);
            Assert.Equal(Tier.Urgent, c.FieldTier);
            Assert.Equal(BoardKind.None, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
        }

        [Fact]
        public void CheckeredIsFieldExclusive()
        {
            var s = S();
            s.Flag = TrackFlag.Checkered;
            s.CountdownLaps = 5;
            s.IncidentWarning = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Checkered, c.Field);
            Assert.Equal(BoardKind.None, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
        }

        [Fact]
        public void CautionForcesYellowFieldUnderTheSafetyCarBoard()
        {
            var s = S();
            s.SafetyCar = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(Tier.Alert, c.FieldTier);
            Assert.Equal(BoardKind.SafetyCar, c.Board);
        }

        [Fact]
        public void BlueYieldsToCautionYellow()
        {
            var s = S();
            s.Flag = TrackFlag.Blue;
            s.Tier = Tier.Alert;
            s.SafetyCar = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(BoardKind.SafetyCar, c.Board);
        }

        [Fact]
        public void BlackKeepsTheFieldUnderCaution()
        {
            var s = S();
            s.BlackFlag = true;
            s.SafetyCar = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Black, c.Field);
            Assert.Equal(BoardKind.SafetyCar, c.Board);
        }

        [Fact]
        public void BlackDemotesToBoardWhenYellowWinsTheField()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Tier = Tier.Alert;
            s.BlackFlag = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(BoardKind.BlackFlag, c.Board);
        }

        [Fact]
        public void DqBoardShowsWhenDemotedToo()
        {
            // DQ is orthogonal: a winning yellow field takes the slot, and
            // the DQ board still says the race is over.
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Disqualified = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(BoardKind.Disqualified, c.Board);
        }

        [Fact]
        public void MeatballDemotesUnderBlackField()
        {
            var s = S();
            s.BlackFlag = true;
            s.Meatball = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Black, c.Field);
            Assert.Equal(BoardKind.MeatballFlag, c.Board);
        }

        [Fact]
        public void DisqualificationSuppressesFrameAdvisories()
        {
            var s = S();
            s.Disqualified = true;
            s.IncidentWarning = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Black, c.Field);
            Assert.Equal(BoardKind.Disqualified, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
        }

        [Fact]
        public void FurledOutranksIncidentInTheFrameSlot()
        {
            var s = S();
            s.Furled = true;
            s.IncidentWarning = true;
            Assert.Equal(FrameKind.Furled, Compositor.Select(s, true).Frame);
            s.Furled = false;
            Assert.Equal(FrameKind.Incident, Compositor.Select(s, true).Frame);
        }

        [Fact]
        public void BoardPrecedenceAmongNotices()
        {
            var s = S();
            s.CountdownLaps = 10;
            s.StartPhase = StartPhase.Ready;
            var c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.Countdown, c.Board);
            Assert.Equal(10, c.BoardValue);

            s.CountdownLaps = 0;
            c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.StartGantry, c.Board);
        }

        [Fact]
        public void MeatballAloneTakesTheField()
        {
            var s = S();
            s.Meatball = true;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Meatball, c.Field);
            Assert.Equal(Tier.Alert, c.FieldTier);
            Assert.Equal(BoardKind.None, c.Board);
        }

        [Fact]
        public void DebrisIsAnOrdinaryLowField()
        {
            var s = S();
            s.Flag = TrackFlag.Debris;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Debris, c.Field);
            Assert.Equal(Tier.Ambient, c.FieldTier);
        }
    }
}
