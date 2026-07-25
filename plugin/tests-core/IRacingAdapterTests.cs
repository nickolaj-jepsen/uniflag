// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// iRacing adapter tests: the game-name gate, every documented SessionFlags
// mapping rule (bit values verified against the iRacingSDK.dll shipped
// inside SimHub 9.11.21 — see docs/simhub-flag-properties.md), and the
// raw-extraction path through StatusDataBase.GetRawDataObject() exercised
// against fakes that mirror the real IRacingReader.DataSampleEx shape (a
// `Telemetry` property whose value derives from Dictionary<string, object>).

using System.Collections.Generic;
using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
    public class IRacingAdapterTests
    {
        [Theory]
        [InlineData("IRacing", true)]
        [InlineData("iracing", true)] // discovery is case-insensitive by contract
        [InlineData("IRACING", true)]
        [InlineData("Ac", false)]
        [InlineData("AssettoCorsaCompetizione", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void MatchesExactlyTheIRacingGameCode(string gameName, bool want)
        {
            Assert.Equal(want, new IRacingAdapter().Matches(gameName));
        }

        /// <summary>
        /// A live iRacing snapshot with unified flags derived from the raw
        /// mask the way SimHub's own reader does (IL-verified against
        /// ICarsReader.dll: Flag_Yellow ⇐ yellow|yellowWaving|caution|
        /// cautionWaving; Flag_Blue ⇐ blue &amp;&amp; !green — green
        /// suppresses unified blue, it is NOT a 1:1 mirror (SimHub issue
        /// #436); White/Green/Checkered/Black mirror their single bits;
        /// Flag_Orange ⇐ repair). Keeping the two layers consistent is what
        /// a real tick looks like.
        /// </summary>
        internal static TelemetrySnapshot IRacingSnapshot(uint sessionFlags)
        {
            return new TelemetrySnapshot
            {
                GameRunning = true,
                GameInMenu = false,
                GamePaused = false,
                HasData = true,
                GameName = IRacingAdapter.IRacingGameName,
                SessionTypeName = "Race",
                FlagYellow = (sessionFlags & (IRacingAdapter.FlagYellow
                    | IRacingAdapter.FlagYellowWaving
                    | IRacingAdapter.FlagCaution
                    | IRacingAdapter.FlagCautionWaving)) != 0,
                FlagBlue = (sessionFlags & IRacingAdapter.FlagBlue) != 0
                    && (sessionFlags & IRacingAdapter.FlagGreen) == 0,
                FlagBlack = (sessionFlags & IRacingAdapter.FlagBlack) != 0,
                FlagWhite = (sessionFlags & IRacingAdapter.FlagWhite) != 0,
                FlagCheckered = (sessionFlags & IRacingAdapter.FlagCheckered) != 0,
                FlagGreen = (sessionFlags & IRacingAdapter.FlagGreen) != 0,
                FlagOrange = (sessionFlags & IRacingAdapter.FlagRepair) != 0,
                HasRawSessionFlags = true,
                RawSessionFlags = sessionFlags,
            };
        }

        private static SignalState MapThroughPipeline(TelemetrySnapshot snapshot)
        {
            return new AdapterPipeline(new IRacingAdapter()).Map(snapshot);
        }

        [Fact]
        public void RedBitSurfacesTheRedFlagTheUnifiedLayerNeverHas()
        {
            SignalState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagRed));
            Assert.Equal(TrackFlag.Red, state.Flag);
            Assert.Equal(Tier.Urgent, state.Tier);
        }

        [Fact]
        public void DisplayedYellowLosesTheGenericAlertGuess()
        {
            // Raw is better than unified here: a displayed (non-waving)
            // yellow renders ambient instead of the generic Alert heuristic.
            SignalState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagYellow));
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Ambient, state.Tier);
        }

        [Fact]
        public void WavingYellowEntersAtAlert()
        {
            SignalState state = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagYellow | IRacingAdapter.FlagYellowWaving));
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);
        }

        [Fact]
        public void WavingCautionEntersAtUrgent()
        {
            SignalState state = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagCaution | IRacingAdapter.FlagCautionWaving));
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Urgent, state.Tier);
        }

        [Theory]
        [InlineData(IRacingAdapter.FlagCaution)]
        [InlineData(IRacingAdapter.FlagCautionWaving)]
        [InlineData(IRacingAdapter.FlagCaution | IRacingAdapter.FlagCautionWaving)]
        public void CautionBitsDeployTheSafetyCarBoard(uint bits)
        {
            // iRacing's full-course caution is a deployed pace car; it has
            // no VSC concept, so the SC board is always the right board.
            SignalState state = MapThroughPipeline(IRacingSnapshot(bits));
            Assert.Equal(Caution.SafetyCar, state.Caution);

            Composition comp = Compositor.Select(state, connected: true);
            Assert.Equal(FieldKind.Yellow, comp.Field);
            Assert.Equal(BoardKind.SafetyCar, comp.Board);
        }

        [Fact]
        public void RepairBitRaisesTheMeatballField()
        {
            SignalState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagRepair));
            Assert.True(state.Meatball);
            // The unified layer maps repair to Flag_Orange, which the generic
            // adapter routes to the same orthogonal Meatball dimension — the
            // track flag stays None and the meatball takes the field.
            Assert.Equal(TrackFlag.None, state.Flag);
            Assert.Equal(FieldKind.Meatball, Compositor.Select(state, connected: true).Field);
        }

        [Fact]
        public void FurledBitRaisesTheWarningFrameOnly()
        {
            SignalState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagFurled));
            Assert.True(state.Furled);
            Assert.Equal(TrackFlag.None, state.Flag);
            Assert.Equal(FrameKind.Furled, Compositor.Select(state, connected: true).Frame);
        }

        [Fact]
        public void BlackBitStaysABareBlackFlag()
        {
            SignalState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagBlack));
            Assert.True(state.BlackFlag);
            // No DT/SG distinction exists in iRacing telemetry — the detail
            // must stay None (never guess a service type).
            Assert.Equal(BlackDetail.None, state.BlackDetail);
            Assert.Equal(FieldKind.Black, Compositor.Select(state, connected: true).Field);
        }

        [Fact]
        public void DisqualifyIsOrthogonalAndSurvivesOtherFlags()
        {
            SignalState alone = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagDisqualify));
            Assert.True(alone.BlackFlag);
            Assert.Equal(BlackDetail.Disqualified, alone.BlackDetail);

            // A concurrent blue keeps the track flag, but the DQ order is
            // never discarded: the black family outranks blue in the field
            // and the DQ board rides on top.
            SignalState withBlue = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagDisqualify | IRacingAdapter.FlagBlue));
            Assert.Equal(TrackFlag.Blue, withBlue.Flag);
            Assert.True(withBlue.BlackFlag);
            Composition comp = Compositor.Select(withBlue, connected: true);
            Assert.Equal(FieldKind.Black, comp.Field);
            Assert.Equal(BoardKind.Disqualified, comp.Board);
        }

        [Fact]
        public void GreenSuppressesUnifiedBlueAndTheAdapterHonoursIt()
        {
            // SimHub derives Flag_Blue = blue && !green (issue #436): on a
            // real blue+green tick — a blue shown to a soon-to-be-lapped
            // car inside the green-flag window — the unified layer reports
            // no blue at all. The adapter deliberately does NOT restore
            // blue from raw: green is the flag that matters there.
            SignalState state = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagBlue | IRacingAdapter.FlagGreen));
            Assert.Equal(TrackFlag.Green, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);

            // Without green, unified blue mirrors the raw bit and enters at
            // Alert (a blue shown to you wants the attention pulse).
            SignalState blueAlone = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagBlue));
            Assert.Equal(TrackFlag.Blue, blueAlone.Flag);
            Assert.Equal(Tier.Alert, blueAlone.Tier);
        }

        [Fact]
        public void GreenHeldIsNotAGreenFlagItArmsTheGantryInstead()
        {
            // greenHeld = the starter holding the green still furled: the
            // real green arrives with the green bit. Showing green here made
            // the panel jump the start, so the bit folds into the gantry's
            // Set phase instead.
            SignalState held = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagGreenHeld));
            Assert.Equal(TrackFlag.None, held.Flag);
            Assert.Equal(StartPhase.Set, held.StartPhase);
            Assert.Equal(BoardKind.StartGantry, Compositor.Select(held, connected: true).Board);

            // The moment the green bit flies, the flag claims the field.
            SignalState green = MapThroughPipeline(IRacingSnapshot(
                IRacingAdapter.FlagGreenHeld | IRacingAdapter.FlagGreen | IRacingAdapter.FlagStartGo));
            Assert.Equal(TrackFlag.Green, green.Flag);
            Assert.Equal(FieldKind.Green, Compositor.Select(green, connected: true).Field);
        }

        [Fact]
        public void MissingRawLayerLeavesTheGenericResultUntouched()
        {
            TelemetrySnapshot snapshot = IRacingSnapshot(IRacingAdapter.FlagYellow);
            snapshot.HasRawSessionFlags = false; // e.g. raw shape drift
            SignalState state = MapThroughPipeline(snapshot);
            // Generic heuristic stands: yellow enters at Alert.
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);
        }

        [Fact]
        public void OtherGamesNeverEnterTheRefiner()
        {
            TelemetrySnapshot snapshot = IRacingSnapshot(IRacingAdapter.FlagRed);
            snapshot.GameName = "AssettoCorsaCompetizione";
            SignalState state = MapThroughPipeline(snapshot);
            // Even with (impossible) raw data present, a non-iRacing game
            // gets the pure generic mapping — never a red flag.
            Assert.NotEqual(TrackFlag.Red, state.Flag);
        }

        [Theory]
        [InlineData(IRacingAdapter.FlagStartGo, StartPhase.Go)]
        [InlineData(IRacingAdapter.FlagStartSet, StartPhase.Set)]
        [InlineData(IRacingAdapter.FlagGreenHeld, StartPhase.Set)]
        [InlineData(IRacingAdapter.FlagStartReady, StartPhase.Ready)]
        [InlineData(IRacingAdapter.FlagOneLapToGreen, StartPhase.Ready)]
        [InlineData(IRacingAdapter.FlagGreenHeld | IRacingAdapter.FlagStartGo, StartPhase.Go)]
        [InlineData(IRacingAdapter.FlagStartHidden, StartPhase.Off)]
        [InlineData(0u, StartPhase.Off)]
        public void StartLightBitsMapToTheGantryPhase(uint bits, StartPhase expected)
        {
            // go > set > ready; the rolling-start oneLapToGreen folds into
            // Ready and greenHeld (furled green in hand) into Set;
            // startHidden and the unset case are Off.
            Assert.Equal(expected, MapThroughPipeline(IRacingSnapshot(bits)).StartPhase);
        }

        [Fact]
        public void GantryBoardRidesAnyFieldUnderTheCompositor()
        {
            SignalState ready = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagStartReady));
            Assert.Equal(BoardKind.StartGantry, Compositor.Select(ready, connected: true).Board);

            // A flag on the same tick takes the field; the gantry keeps the
            // board slot — the compositor stacks them instead of choosing.
            SignalState withYellow = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagStartReady | IRacingAdapter.FlagYellow));
            Composition comp = Compositor.Select(withYellow, connected: true);
            Assert.Equal(FieldKind.Yellow, comp.Field);
            Assert.Equal(BoardKind.StartGantry, comp.Board);
        }

        [Fact]
        public void DebrisBitIsTheLowestTrackFlag()
        {
            SignalState alone = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagDebris));
            Assert.Equal(TrackFlag.Debris, alone.Flag);
            Assert.Equal(FieldKind.Debris, Compositor.Select(alone, connected: true).Field);

            // A unified flag (which already conveys caution) supersedes it.
            SignalState withYellow = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagDebris | IRacingAdapter.FlagYellow));
            Assert.Equal(TrackFlag.Yellow, withYellow.Flag);
        }

        [Theory]
        [InlineData(IRacingAdapter.FlagTenToGo, 10)]
        [InlineData(IRacingAdapter.FlagFiveToGo, 5)]
        [InlineData(IRacingAdapter.FlagTenToGo | IRacingAdapter.FlagFiveToGo, 10)]
        [InlineData(0u, 0)]
        public void CountdownBitsMapToTheNoticeBoards(uint bits, int laps)
        {
            Assert.Equal(laps, MapThroughPipeline(IRacingSnapshot(bits)).CountdownLaps);
        }

        /// <summary>An iRacing snapshot carrying incident count/limit dimensions.</summary>
        private static TelemetrySnapshot IncidentSnapshot(bool hasCount, int count, bool hasLimit, int limit)
        {
            TelemetrySnapshot snapshot = IRacingSnapshot(0);
            snapshot.HasIncidentCount = hasCount;
            snapshot.IncidentCount = count;
            snapshot.HasIncidentLimit = hasLimit;
            snapshot.IncidentLimit = limit;
            return snapshot;
        }

        [Fact]
        public void IncidentWarningFiresWithinTheMarginOfTheLimit()
        {
            // Margin 4, limit 17 → warn once the count reaches 13.
            Assert.True(MapThroughPipeline(IncidentSnapshot(true, 13, true, 17)).IncidentWarning);
            Assert.True(MapThroughPipeline(IncidentSnapshot(true, 17, true, 17)).IncidentWarning);
            Assert.False(MapThroughPipeline(IncidentSnapshot(true, 12, true, 17)).IncidentWarning);
        }

        [Fact]
        public void IncidentWarningStaysOffWithoutBothCountAndFiniteLimit()
        {
            // "unlimited" → HasIncidentLimit false → never warn, any count.
            Assert.False(MapThroughPipeline(IncidentSnapshot(true, 999, false, 0)).IncidentWarning);
            // No count sample → never warn.
            Assert.False(MapThroughPipeline(IncidentSnapshot(false, 0, true, 17)).IncidentWarning);
            // A zero/negative limit is not a real limit.
            Assert.False(MapThroughPipeline(IncidentSnapshot(true, 5, true, 0)).IncidentWarning);
        }

        [Fact]
        public void IncidentWarningIsIndependentOfTheSessionFlagsMask()
        {
            // The mask can drop out (shape drift) while incident data survives
            // — the warning is derived before the mask guard, so it still fires.
            TelemetrySnapshot snapshot = IncidentSnapshot(true, 15, true, 17);
            snapshot.HasRawSessionFlags = false;
            Assert.True(MapThroughPipeline(snapshot).IncidentWarning);
        }
    }

}
