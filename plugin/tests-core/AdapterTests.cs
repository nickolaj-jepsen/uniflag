// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter-layer tests: the generic unified Flag_* mapping matrix, the
// track-flag priority order, the orthogonal black/meatball dimensions, the
// session-name mapping, the no-game predicate, the pipeline's refiner seam,
// and the SimHub-typed extractor. Everything except GameDataExtractorTests
// is SimHub-free.

using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;
using SectorSet = Uniflag.Rendering.SectorSet;
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
            SignalState state = SignalState.Default;
            new GenericAdapter().Map(snapshot, ref state);
            return state;
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
            Assert.Equal(Caution.None, state.Caution);
            Assert.True(state.Sectors.IsEmpty);
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
            Assert.Equal(BlackDetail.None, state.BlackDetail);
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

            Composition comp = Compositor.Select(state, connected: true);
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
        // docs/simhub-flag-properties.md ("Generic adapter mapping").
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
        public void RefinerOnlyDimensionsAreAlwaysCleared()
        {
            // The generic adapter has no raw-data layers, so it must pin
            // caution/sectors/notices/advisories to their defaults even when
            // a refiner-less pipeline reuses a dirty state.
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            var state = new SignalState
            {
                Flag = TrackFlag.Red,
                Tier = Tier.Urgent,
                Session = Session.Replay,
                Caution = Caution.SafetyCar,
                Sectors = SectorSet.FromBits(0b111),
                BlackDetail = BlackDetail.StopAndGo,
                StartPhase = StartPhase.Set,
                TimePenaltySeconds = 5,
                CountdownLaps = 10,
                Furled = true,
                IncidentWarning = true,
            };
            new GenericAdapter().Map(snapshot, ref state);
            Assert.Equal(Caution.None, state.Caution);
            Assert.True(state.Sectors.IsEmpty);
            Assert.Equal(BlackDetail.None, state.BlackDetail);
            Assert.Equal(StartPhase.Off, state.StartPhase);
            Assert.Equal(0, state.TimePenaltySeconds);
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

    public class AdapterPipelineTests
    {
        /// <summary>Refiner stand-in: overrides the tier for one game only.</summary>
        private sealed class UrgentTierRefiner : IGameAdapter
        {
            public bool Matches(string gameName) => gameName == "IRacing";

            public void Map(TelemetrySnapshot snapshot, ref SignalState state)
            {
                state.Tier = Tier.Urgent;
            }
        }

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
        public void DefaultPipelineIsGenericOnly()
        {
            SignalState state = new AdapterPipeline().Map(YellowSnapshot("Ac"));
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Tier.Alert, state.Tier);
            Assert.Equal(Session.Racing, state.Session);
        }

        [Fact]
        public void MatchingRefinerOverridesTheGenericResult()
        {
            var pipeline = new AdapterPipeline(new UrgentTierRefiner());
            SignalState state = pipeline.Map(YellowSnapshot("IRacing"));
            // The refiner ran after the generic adapter: tier overridden,
            // everything else kept.
            Assert.Equal(Tier.Urgent, state.Tier);
            Assert.Equal(TrackFlag.Yellow, state.Flag);
            Assert.Equal(Session.Racing, state.Session);
        }

        [Fact]
        public void NonMatchingRefinerLeavesTheGenericResultAlone()
        {
            var pipeline = new AdapterPipeline(new UrgentTierRefiner());
            SignalState state = pipeline.Map(YellowSnapshot("Ac"));
            Assert.Equal(Tier.Alert, state.Tier);
        }
    }
}
