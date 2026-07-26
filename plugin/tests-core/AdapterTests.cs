// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter-layer tests: the generic unified Flag_* mapping matrix, the
// track-flag priority order, the orthogonal black/meatball dimensions, the
// session-name mapping, the no-game predicate, and the refiner gate.

using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;
using Session = Uniflag.Rendering.Session;

namespace Uniflag.Tests
{
    public class GenericAdapterTests
    {
        /// <summary>A snapshot representing a live race with no flags set.</summary>
        private static TelemetrySnapshot Live()
        {
            return new TelemetrySnapshot
            {
                GameRunning = true,
                GameInMenu = false,
                GamePaused = false,
                HasData = true,
                GameName = "TestGame",
                SessionTypeName = "Race",
            };
        }

        private static void SetFlag(TelemetrySnapshot snapshot, TrackFlag flag)
        {
            switch (flag)
            {
                case TrackFlag.Yellow:
                    snapshot.FlagYellow = true;
                    break;
                case TrackFlag.Blue:
                    snapshot.FlagBlue = true;
                    break;
                case TrackFlag.White:
                    snapshot.FlagWhite = true;
                    break;
                case TrackFlag.Checkered:
                    snapshot.FlagCheckered = true;
                    break;
                case TrackFlag.Green:
                    snapshot.FlagGreen = true;
                    break;
            }
        }

        private static SignalState Map(TelemetrySnapshot snapshot)
        {
            return GenericAdapter.Map(snapshot);
        }

        [Theory]
        [InlineData(TrackFlag.Yellow, Tier.Alert)]
        [InlineData(TrackFlag.Blue, Tier.Ambient)]
        [InlineData(TrackFlag.White, Tier.Ambient)]
        [InlineData(TrackFlag.Checkered, Tier.Ambient)]
        [InlineData(TrackFlag.Green, Tier.Ambient)]
        public void EachUnifiedTrackFlagAloneMapsDirectly(TrackFlag flag, Tier wantTier)
        {
            TelemetrySnapshot snapshot = Live();
            SetFlag(snapshot, flag);
            SignalState state = Map(snapshot);
            Assert.Equal(flag, state.Flag);
            // The unified layer cannot distinguish displayed from waved —
            // only yellow gets the documented Alert-tier heuristic.
            Assert.Equal(wantTier, state.Tier);
            Assert.Equal(Session.Racing, state.Session);
            Assert.False(state.SafetyCar);
            Assert.False(state.BlackFlag);
            Assert.False(state.Meatball);
        }

        [Fact]
        public void UnifiedBlackMapsToTheOrthogonalOrderDimension()
        {
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagBlack = true;
            SignalState state = Map(snapshot);
            Assert.True(state.BlackFlag);
            Assert.False(state.Disqualified);
            Assert.Equal(TrackFlag.None, state.Flag);
        }

        [Fact]
        public void UnifiedOrangeMapsToTheMeatballDimension()
        {
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagOrange = true;
            SignalState state = Map(snapshot);
            Assert.True(state.Meatball);
            Assert.Equal(TrackFlag.None, state.Flag);
        }

        [Fact]
        public void NoFlagSetMapsToNone()
        {
            SignalState state = Map(Live());
            Assert.Equal(TrackFlag.None, state.Flag);
            Assert.Equal(Tier.Ambient, state.Tier);
            Assert.False(state.BlackFlag);
            Assert.False(state.Meatball);
        }

        // Track-flag priority: Yellow > Blue > White > Checkered > Green.
        [Theory]
        [InlineData(TrackFlag.Yellow, TrackFlag.Blue)]
        [InlineData(TrackFlag.Blue, TrackFlag.White)]
        [InlineData(TrackFlag.White, TrackFlag.Checkered)]
        [InlineData(TrackFlag.Checkered, TrackFlag.Green)]
        public void AdjacentPriorityPairsResolveToTheHigherFlag(TrackFlag higher, TrackFlag lower)
        {
            TelemetrySnapshot snapshot = Live();
            SetFlag(snapshot, higher);
            SetFlag(snapshot, lower);
            Assert.Equal(higher, Map(snapshot).Flag);
        }

        [Fact]
        public void BlackAndOrangeRideAlongWithAWinningTrackFlag()
        {
            // The point of the orthogonal dimensions: a yellow win no longer
            // discards the driver-directed orders — the compositor's
            // demotion rule needs to see them.
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            snapshot.FlagBlack = true;
            snapshot.FlagOrange = true;
            SignalState state = Map(snapshot);
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.True(state.BlackFlag);
            Assert.True(state.Meatball);

            Composition comp = Compositor.Select(state);
            Assert.Equal(FieldKind.Yellow, comp.Field);
            Assert.Equal(BoardKind.BlackFlag, comp.Board);
        }

        [Fact]
        public void AllFlagsSetResolveToYellowPlusTheOrders()
        {
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            snapshot.FlagBlue = true;
            snapshot.FlagBlack = true;
            snapshot.FlagWhite = true;
            snapshot.FlagCheckered = true;
            snapshot.FlagGreen = true;
            snapshot.FlagOrange = true;
            SignalState state = Map(snapshot);
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);
            Assert.True(state.BlackFlag);
            Assert.True(state.Meatball);
        }

        // Session vocabulary, per the mapping documented in
        // plugin/core/Adapters/GenericAdapter.cs (MapSession).
        [Theory]
        [InlineData("Race", Session.Racing)]
        [InlineData("RACE", Session.Racing)]
        [InlineData("Race 1", Session.Racing)]
        [InlineData("Practice", Session.PreRace)]
        [InlineData("Open Practice", Session.PreRace)]
        [InlineData("Offline Testing", Session.PreRace)]
        [InlineData("Qualify", Session.PreRace)]
        [InlineData("Qualifying", Session.PreRace)]
        [InlineData("Lone Qualify", Session.PreRace)]
        [InlineData("QUALIFY", Session.PreRace)]
        [InlineData("Superpole", Session.PreRace)]
        [InlineData("Hotlap", Session.PreRace)]
        [InlineData("HOTSTINT", Session.PreRace)]
        [InlineData("Warmup", Session.PreRace)]
        [InlineData(null, Session.Unknown)]
        [InlineData("", Session.Unknown)]
        [InlineData("Garage", Session.Unknown)]
        public void SessionTypeNameMapsPerContract(string sessionTypeName, Session want)
        {
            Assert.Equal(want, GenericAdapter.MapSession(sessionTypeName, gamePaused: false));

            TelemetrySnapshot snapshot = Live();
            snapshot.SessionTypeName = sessionTypeName;
            Assert.Equal(want, Map(snapshot).Session);
        }

        [Theory]
        [InlineData("Race")]
        [InlineData("Practice")]
        [InlineData(null)]
        public void GamePausedWinsOverEverySessionName(string sessionTypeName)
        {
            Assert.Equal(Session.Paused, GenericAdapter.MapSession(sessionTypeName, gamePaused: true));
        }

        [Fact]
        public void RefinerOnlyDimensionsStayAtTheirDefaults()
        {
            // The generic adapter has no raw-data layers; producing from
            // SignalState.Default is what keeps the refiner-only dimensions
            // clear (safety car, DQ, start sequence, notices, advisories).
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            SignalState state = GenericAdapter.Map(snapshot);
            Assert.False(state.SafetyCar);
            Assert.False(state.Disqualified);
            Assert.Equal(StartPhase.Off, state.StartPhase);
            Assert.Equal(0, state.CountdownLaps);
            Assert.False(state.Furled);
            Assert.False(state.IncidentWarning);
        }
    }

    public class NoGamePredicateTests
    {
        private static TelemetrySnapshot Snapshot(bool running, bool inMenu, bool hasData, bool paused = false)
        {
            return new TelemetrySnapshot
            {
                GameRunning = running,
                GameInMenu = inMenu,
                HasData = hasData,
                GamePaused = paused,
            };
        }

        [Fact]
        public void LiveNeedsRunningOutOfMenuWithData()
        {
            Assert.True(Snapshot(running: true, inMenu: false, hasData: true).HasLiveSession);
        }

        [Theory]
        [InlineData(false, false, true)] // game not running
        [InlineData(true, true, true)] // sitting in the menus
        [InlineData(true, false, false)] // no telemetry block yet
        [InlineData(false, true, false)] // nothing at all
        public void AnyMissingLegMeansConnectedIdle(bool running, bool inMenu, bool hasData)
        {
            Assert.False(Snapshot(running, inMenu, hasData).HasLiveSession);
        }

        [Fact]
        public void PausedStillCountsAsLive()
        {
            // Paused maps to Session.Paused (the static race idle), not to
            // the connected-idle marker — the game is there, just held.
            Assert.True(Snapshot(running: true, inMenu: false, hasData: true, paused: true).HasLiveSession);
        }
    }

    public class SignalMappingTests
    {
        private static TelemetrySnapshot YellowSnapshot(string gameName)
        {
            return new TelemetrySnapshot
            {
                GameRunning = true,
                HasData = true,
                GameName = gameName,
                SessionTypeName = "Race",
                FlagYellow = true,
            };
        }

        [Fact]
        public void ANonIRacingGameGetsTheGenericMappingOnly()
        {
            SignalState state = SignalMapping.Map(YellowSnapshot("Ac"));
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);
            Assert.Equal(Session.Racing, state.Session);
        }

        [Fact]
        public void TheIRacingRefinerRunsOnlyForIRacing()
        {
            // A displayed-yellow raw mask drops the tier to Ambient if the
            // refiner runs — so a populated raw field under another game name
            // must leave the generic Alert guess standing.
            TelemetrySnapshot snapshot = YellowSnapshot("Ac");
            snapshot.HasRawSessionFlags = true;
            snapshot.RawSessionFlags = IRacingAdapter.FlagYellow;
            Assert.Equal(Tier.Alert, SignalMapping.Map(snapshot).Tier);

            snapshot.GameName = IRacingAdapter.IRacingGameName;
            Assert.Equal(Tier.Ambient, SignalMapping.Map(snapshot).Tier);
        }
    }
}
