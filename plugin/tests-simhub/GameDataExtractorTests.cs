// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// GameDataExtractor: the one place on the telemetry path that touches
// GameReaderCommon. The mapping itself is snapshot-based and lives in
// plugin/tests-core/AdapterTests.cs.

using GameReaderCommon;
using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
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
