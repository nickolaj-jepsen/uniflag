// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter-layer tests (docs/v2-plan.md M4 step 2): the generic unified
// Flag_* mapping matrix, the flag priority order, the session-name mapping,
// the no-game predicate, the pipeline's refiner seam, and the SimHub-typed
// extractor. Everything except GameDataExtractorTests is SimHub-free.

using GameReaderCommon;
using Uniflag.Adapters;
using Uniflag.Rendering;
using Xunit;

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

        private static void SetFlag(TelemetrySnapshot snapshot, Flag flag)
        {
            switch (flag)
            {
                case Flag.Yellow:
                    snapshot.FlagYellow = true;
                    break;
                case Flag.Blue:
                    snapshot.FlagBlue = true;
                    break;
                case Flag.Black:
                    snapshot.FlagBlack = true;
                    break;
                case Flag.White:
                    snapshot.FlagWhite = true;
                    break;
                case Flag.Checkered:
                    snapshot.FlagCheckered = true;
                    break;
                case Flag.Green:
                    snapshot.FlagGreen = true;
                    break;
                case Flag.Orange:
                    snapshot.FlagOrange = true;
                    break;
            }
        }

        private static RenderState Map(TelemetrySnapshot snapshot)
        {
            RenderState state = RenderState.Default;
            new GenericAdapter().Map(snapshot, ref state);
            return state;
        }

        [Theory]
        [InlineData(Flag.Yellow, WaveLevel.Single)]
        [InlineData(Flag.Blue, WaveLevel.None)]
        [InlineData(Flag.Black, WaveLevel.None)]
        [InlineData(Flag.White, WaveLevel.None)]
        [InlineData(Flag.Checkered, WaveLevel.None)]
        [InlineData(Flag.Green, WaveLevel.None)]
        [InlineData(Flag.Orange, WaveLevel.None)]
        public void EachUnifiedFlagAloneMapsDirectly(Flag flag, WaveLevel wantWave)
        {
            TelemetrySnapshot snapshot = Live();
            SetFlag(snapshot, flag);
            RenderState state = Map(snapshot);
            Assert.Equal(flag, state.Flag);
            // The unified layer cannot distinguish displayed from waved —
            // only yellow gets the documented Single-wave heuristic.
            Assert.Equal(wantWave, state.Wave);
            Assert.Equal(Session.Racing, state.Session);
            Assert.Equal(Caution.None, state.Caution);
            Assert.True(state.Sectors.IsEmpty);
        }

        [Fact]
        public void NoFlagSetMapsToNone()
        {
            RenderState state = Map(Live());
            Assert.Equal(Flag.None, state.Flag);
            Assert.Equal(WaveLevel.None, state.Wave);
        }

        // Priority parity with the v1 NCalc formula (simhub/README.md):
        // Yellow > Blue > Black > White > Checkered > Green > Orange.
        [Theory]
        [InlineData(Flag.Yellow, Flag.Blue)]
        [InlineData(Flag.Blue, Flag.Black)]
        [InlineData(Flag.Black, Flag.White)]
        [InlineData(Flag.White, Flag.Checkered)]
        [InlineData(Flag.Checkered, Flag.Green)]
        [InlineData(Flag.Green, Flag.Orange)]
        public void AdjacentPriorityPairsResolveToTheHigherFlag(Flag higher, Flag lower)
        {
            TelemetrySnapshot snapshot = Live();
            SetFlag(snapshot, higher);
            SetFlag(snapshot, lower);
            Assert.Equal(higher, Map(snapshot).Flag);
        }

        [Fact]
        public void AllFlagsSetResolveToYellow()
        {
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            snapshot.FlagBlue = true;
            snapshot.FlagBlack = true;
            snapshot.FlagWhite = true;
            snapshot.FlagCheckered = true;
            snapshot.FlagGreen = true;
            snapshot.FlagOrange = true;
            RenderState state = Map(snapshot);
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(WaveLevel.Single, state.Wave);
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
        public void CautionAndSectorsAreAlwaysCleared()
        {
            // Raw-data layers (VSC/SC, sector yellows) arrive with M10 —
            // until then the generic adapter must pin them to None/Empty
            // even if a refiner-less pipeline reuses a dirty state.
            TelemetrySnapshot snapshot = Live();
            snapshot.FlagYellow = true;
            var state = new RenderState
            {
                Flag = Flag.Red,
                Wave = WaveLevel.Double,
                Session = Session.Replay,
                Caution = Caution.SafetyCar,
                Sectors = SectorSet.FromBits(0b111),
            };
            new GenericAdapter().Map(snapshot, ref state);
            Assert.Equal(Caution.None, state.Caution);
            Assert.True(state.Sectors.IsEmpty);
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
            // Paused maps to Session.Paused (race-idle marker), not to the
            // connected-idle marker — the game is there, just held.
            Assert.True(Snapshot(running: true, inMenu: false, hasData: true, paused: true).HasLiveSession);
        }
    }

    public class AdapterPipelineTests
    {
        /// <summary>M10 stand-in: refines the wave level for one game only.</summary>
        private sealed class DoubleWaveRefiner : IGameAdapter
        {
            public bool Matches(string gameName) => gameName == "IRacing";

            public void Map(TelemetrySnapshot snapshot, ref RenderState state)
            {
                state.Wave = WaveLevel.Double;
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
            RenderState state = new AdapterPipeline().Map(YellowSnapshot("Ac"));
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(WaveLevel.Single, state.Wave);
            Assert.Equal(Session.Racing, state.Session);
        }

        [Fact]
        public void MatchingRefinerOverridesTheGenericResult()
        {
            var pipeline = new AdapterPipeline(new DoubleWaveRefiner());
            RenderState state = pipeline.Map(YellowSnapshot("IRacing"));
            // The refiner ran after the generic adapter: wave overridden,
            // everything else kept.
            Assert.Equal(WaveLevel.Double, state.Wave);
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(Session.Racing, state.Session);
        }

        [Fact]
        public void NonMatchingRefinerLeavesTheGenericResultAlone()
        {
            var pipeline = new AdapterPipeline(new DoubleWaveRefiner());
            RenderState state = pipeline.Map(YellowSnapshot("Ac"));
            Assert.Equal(WaveLevel.Single, state.Wave);
        }
    }

    /// <summary>
    /// The one SimHub-typed suite. <c>StatusDataBase</c> is abstract with
    /// one abstract member (<c>GetRawDataObject</c>), and both it and
    /// <c>GameData</c> keep their property setters <c>internal</c> — so the
    /// test subclasses it and writes through the non-public setters via
    /// reflection. Brittle-by-design tradeoff: this pins the extractor to
    /// the real GameReaderCommon members it reads in production.
    /// </summary>
    public class GameDataExtractorTests
    {
        private sealed class TestStatusData : StatusDataBase
        {
            public override object GetRawDataObject() => null;
        }

        /// <summary>Write through a (possibly internal) property setter.</summary>
        private static void Set(object target, string property, object value)
        {
            System.Reflection.PropertyInfo info = target.GetType().GetProperty(property);
            Assert.NotNull(info);
            info.SetMethod.Invoke(target, new[] { value });
        }

        [Fact]
        public void CopiesGameAndTelemetryFields()
        {
            var data = new GameData();
            Set(data, nameof(GameData.GameRunning), true);
            Set(data, nameof(GameData.GameInMenu), false);
            Set(data, nameof(GameData.GamePaused), true);
            Set(data, nameof(GameData.GameName), "IRacing");
            var telemetry = new TestStatusData();
            Set(telemetry, nameof(StatusDataBase.SessionTypeName), "Race");
            Set(telemetry, nameof(StatusDataBase.Flag_Yellow), 1);
            Set(telemetry, nameof(StatusDataBase.Flag_Black), 1);
            Set(telemetry, nameof(StatusDataBase.Flag_Orange), 1);
            data.NewData = telemetry;

            var snapshot = new TelemetrySnapshot();
            GameDataExtractor.Extract(ref data, snapshot);

            Assert.True(snapshot.GameRunning);
            Assert.False(snapshot.GameInMenu);
            Assert.True(snapshot.GamePaused);
            Assert.Equal("IRacing", snapshot.GameName);
            Assert.True(snapshot.HasData);
            Assert.Equal("Race", snapshot.SessionTypeName);
            Assert.True(snapshot.FlagYellow);
            Assert.False(snapshot.FlagBlue);
            Assert.True(snapshot.FlagBlack);
            Assert.False(snapshot.FlagWhite);
            Assert.False(snapshot.FlagCheckered);
            Assert.False(snapshot.FlagGreen);
            Assert.True(snapshot.FlagOrange);
            Assert.True(snapshot.HasLiveSession);
        }

        [Fact]
        public void NullTelemetryBlockClearsTheSessionFields()
        {
            // Pre-dirty the reused snapshot: a tick without NewData must not
            // leak the previous tick's flags through.
            var snapshot = new TelemetrySnapshot
            {
                HasData = true,
                SessionTypeName = "Race",
                FlagYellow = true,
                FlagGreen = true,
            };
            var data = new GameData();
            Set(data, nameof(GameData.GameRunning), true);
            Set(data, nameof(GameData.GameName), "IRacing");
            Assert.Null(data.NewData); // fresh GameData carries no telemetry

            GameDataExtractor.Extract(ref data, snapshot);

            Assert.True(snapshot.GameRunning);
            Assert.False(snapshot.HasData);
            Assert.Null(snapshot.SessionTypeName);
            Assert.False(snapshot.FlagYellow);
            Assert.False(snapshot.FlagGreen);
            Assert.False(snapshot.HasLiveSession);
        }

        [Fact]
        public void NullGameDataClearsEverything()
        {
            var snapshot = new TelemetrySnapshot
            {
                GameRunning = true,
                HasData = true,
                GameName = "IRacing",
                FlagYellow = true,
            };
            GameData data = null;

            GameDataExtractor.Extract(ref data, snapshot);

            Assert.False(snapshot.GameRunning);
            Assert.False(snapshot.HasData);
            Assert.Null(snapshot.GameName);
            Assert.False(snapshot.FlagYellow);
            Assert.False(snapshot.HasLiveSession);
        }
    }
}
