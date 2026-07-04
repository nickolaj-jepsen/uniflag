// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Slot-selection tests for the Grammar compositor (docs/flag-grammar.md §5):
// takeovers, the demotion rule, caution-forced yellow, within-slot
// precedence, and strip/frame suppression.

using Uniflag.Rendering.Grammar;
using Xunit;
using SectorSet = Uniflag.Rendering.SectorSet;

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
            Assert.False(c.SectorStrip);
        }

        [Fact]
        public void RedIsATotalTakeover()
        {
            var s = S();
            s.Flag = TrackFlag.Red;
            s.Caution = Caution.SafetyCar;
            s.Furled = true;
            s.IncidentWarning = true;
            s.Sectors = SectorSet.Empty.With(1);
            s.StartPhase = StartPhase.Ready;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Red, c.Field);
            Assert.Equal(Tier.Urgent, c.FieldTier);
            Assert.Equal(BoardKind.None, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
            Assert.False(c.SectorStrip);
        }

        [Fact]
        public void CheckeredIsFieldExclusive()
        {
            var s = S();
            s.Flag = TrackFlag.Checkered;
            s.TimePenaltySeconds = 5;
            s.IncidentWarning = true;
            s.Sectors = SectorSet.Empty.With(3);
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Checkered, c.Field);
            Assert.Equal(BoardKind.None, c.Board);
            Assert.Equal(FrameKind.None, c.Frame);
            Assert.False(c.SectorStrip);
        }

        [Fact]
        public void CautionForcesYellowFieldUnderRegimeBoard()
        {
            var s = S();
            s.Caution = Caution.FullCourseYellow;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(Tier.Alert, c.FieldTier);
            Assert.Equal(BoardKind.FullCourseYellow, c.Board);
        }

        [Fact]
        public void BlueYieldsToCautionYellow()
        {
            var s = S();
            s.Flag = TrackFlag.Blue;
            s.Tier = Tier.Alert;
            s.Caution = Caution.SafetyCar;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(BoardKind.SafetyCar, c.Board);
        }

        [Fact]
        public void BlackKeepsTheFieldUnderCaution()
        {
            var s = S();
            s.BlackFlag = true;
            s.Caution = Caution.SafetyCar;
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
        public void ServiceDetailBoardShowsOverOwnBlackField()
        {
            var s = S();
            s.BlackFlag = true;
            s.BlackDetail = BlackDetail.StopAndGo;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Black, c.Field);
            Assert.Equal(BoardKind.StopAndGo, c.Board);
        }

        [Fact]
        public void ServiceDetailBoardShowsWhenDemotedToo()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.BlackDetail = BlackDetail.DriveThrough;
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Yellow, c.Field);
            Assert.Equal(BoardKind.DriveThrough, c.Board);
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
            s.BlackDetail = BlackDetail.Disqualified;
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
        public void SectorStripShowsForLocalYellowOnly()
        {
            var s = S();
            s.Flag = TrackFlag.Yellow;
            s.Sectors = SectorSet.Empty.With(2);
            Assert.True(Compositor.Select(s, true).SectorStrip);

            s.Caution = Caution.SafetyCar;
            Assert.False(Compositor.Select(s, true).SectorStrip);
        }

        [Fact]
        public void SectorStripSurvivesDriverDirectedFields()
        {
            var s = S();
            s.BlackFlag = true;
            s.Sectors = SectorSet.Empty.With(1).With(3);
            var c = Compositor.Select(s, true);
            Assert.Equal(FieldKind.Black, c.Field);
            Assert.True(c.SectorStrip);
        }

        [Fact]
        public void BoardPrecedenceAmongNotices()
        {
            var s = S();
            s.TimePenaltySeconds = 5;
            s.CountdownLaps = 10;
            s.StartPhase = StartPhase.Ready;
            var c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.TimePenalty, c.Board);
            Assert.Equal(5, c.BoardValue);

            s.TimePenaltySeconds = 0;
            c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.Countdown, c.Board);
            Assert.Equal(10, c.BoardValue);

            s.CountdownLaps = 0;
            c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.StartGantry, c.Board);
        }

        [Fact]
        public void GantryCarriesTheLitCount()
        {
            var s = S();
            s.StartPhase = StartPhase.Set;
            s.StartLightsLit = 3;
            var c = Compositor.Select(s, true);
            Assert.Equal(BoardKind.StartGantry, c.Board);
            Assert.Equal(3, c.BoardValue);
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
