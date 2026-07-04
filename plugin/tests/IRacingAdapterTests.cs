// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// iRacing adapter tests (docs/v2-plan.md M10 step 2): the game-name gate,
// every documented SessionFlags mapping rule (bit values verified against
// the iRacingSDK.dll shipped inside SimHub 9.11.21 — see
// docs/simhub-flag-properties.md), and the raw-extraction path through
// StatusDataBase.GetRawDataObject() exercised against fakes that mirror
// the real IRacingReader.DataSampleEx shape (a `Telemetry` property whose
// value derives from Dictionary<string, object>).

using System.Collections.Generic;
using GameReaderCommon;
using Uniflag.Adapters;
using Uniflag.Rendering;
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

        private static RenderState MapThroughPipeline(TelemetrySnapshot snapshot)
        {
            return new AdapterPipeline(new IRacingAdapter()).Map(snapshot);
        }

        [Fact]
        public void RedBitSurfacesTheRedFlagTheUnifiedLayerNeverHas()
        {
            RenderState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagRed));
            Assert.Equal(Flag.Red, state.Flag);
            Assert.Equal(WaveLevel.None, state.Wave);
        }

        [Fact]
        public void DisplayedYellowLosesTheGenericSingleWaveGuess()
        {
            // Raw is better than unified here: a displayed (non-waving)
            // yellow renders static instead of the generic Single heuristic.
            RenderState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagYellow));
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(WaveLevel.None, state.Wave);
        }

        [Fact]
        public void WavingYellowKeepsTheSingleWave()
        {
            RenderState state = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagYellow | IRacingAdapter.FlagYellowWaving));
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(WaveLevel.Single, state.Wave);
        }

        [Theory]
        [InlineData(IRacingAdapter.FlagCaution)]
        [InlineData(IRacingAdapter.FlagCautionWaving)]
        [InlineData(IRacingAdapter.FlagCaution | IRacingAdapter.FlagCautionWaving)]
        public void CautionBitsDeployTheSafetyCarBoard(uint bits)
        {
            // iRacing's full-course caution is a deployed pace car; it has
            // no VSC concept, so the SC board is always the right board.
            RenderState state = MapThroughPipeline(IRacingSnapshot(bits));
            Assert.Equal(Caution.SafetyCar, state.Caution);
            Assert.Equal(
                RenderLayer.SafetyCarBoard,
                Precedence.Select(state, connected: true));
        }

        [Fact]
        public void RepairBitRaisesTheMeatballOverTheUnifiedOrange()
        {
            RenderState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagRepair));
            Assert.True(state.Meatball);
            // The unified layer maps repair to Flag_Orange; the generic
            // result keeps it, but precedence shows the meatball board.
            Assert.Equal(Flag.Orange, state.Flag);
            Assert.Equal(RenderLayer.MeatballBoard, Precedence.Select(state, connected: true));
        }

        [Fact]
        public void FurledBitRaisesTheWarningAccentOnly()
        {
            RenderState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagFurled));
            Assert.True(state.Furled);
            Assert.Equal(Flag.None, state.Flag);
            // No fabricated slowdown: iRacing exposes no graded meter.
            Assert.Equal(0, state.Slowdown);
            Assert.True(Precedence.FurledAccentVisible(state, connected: true));
        }

        [Fact]
        public void BlackBitStaysAPlainBlackFlag()
        {
            RenderState state = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagBlack));
            Assert.Equal(Flag.Black, state.Flag);
            // No DT/SG distinction exists in iRacing telemetry — the detail
            // must stay None (never guess a service type).
            Assert.Equal(BlackFlagDetail.None, state.BlackDetail);
        }

        [Fact]
        public void DisqualifyEnrichesToBlackOnlyWhenNothingElseClaimsTheBase()
        {
            RenderState alone = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagDisqualify));
            Assert.Equal(Flag.Black, alone.Flag);

            // A unified-visible flag keeps its generic priority win.
            RenderState withBlue = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagDisqualify | IRacingAdapter.FlagBlue));
            Assert.Equal(Flag.Blue, withBlue.Flag);
        }

        [Fact]
        public void GreenSuppressesUnifiedBlueAndTheAdapterHonoursIt()
        {
            // SimHub derives Flag_Blue = blue && !green (issue #436): on a
            // real blue+green tick — a blue shown to a soon-to-be-lapped
            // car inside the green-flag window — the unified layer reports
            // no blue at all. The adapter deliberately does NOT restore
            // blue from raw: green is the flag that matters there.
            RenderState state = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagBlue | IRacingAdapter.FlagGreen));
            Assert.Equal(Flag.Green, state.Flag);

            // Without green, unified blue mirrors the raw bit and wins the
            // generic priority over green-less lower flags as usual.
            RenderState blueAlone = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagBlue));
            Assert.Equal(Flag.Blue, blueAlone.Flag);
        }

        [Fact]
        public void GreenHeldEnrichesToGreenOnlyWhenNothingElseClaimsTheBase()
        {
            RenderState alone = MapThroughPipeline(IRacingSnapshot(IRacingAdapter.FlagGreenHeld));
            Assert.Equal(Flag.Green, alone.Flag);

            RenderState withWhite = MapThroughPipeline(
                IRacingSnapshot(IRacingAdapter.FlagGreenHeld | IRacingAdapter.FlagWhite));
            Assert.Equal(Flag.White, withWhite.Flag);
        }

        [Fact]
        public void MissingRawLayerLeavesTheGenericResultUntouched()
        {
            TelemetrySnapshot snapshot = IRacingSnapshot(IRacingAdapter.FlagYellow);
            snapshot.HasRawSessionFlags = false; // e.g. raw shape drift
            RenderState state = MapThroughPipeline(snapshot);
            // Generic heuristic stands: yellow renders single-waved.
            Assert.Equal(Flag.Yellow, state.Flag);
            Assert.Equal(WaveLevel.Single, state.Wave);
        }

        [Fact]
        public void OtherGamesNeverEnterTheRefiner()
        {
            TelemetrySnapshot snapshot = IRacingSnapshot(IRacingAdapter.FlagRed);
            snapshot.GameName = "AssettoCorsaCompetizione";
            RenderState state = MapThroughPipeline(snapshot);
            // Even with (impossible) raw data present, a non-iRacing game
            // gets the pure generic mapping — never a red flag.
            Assert.NotEqual(Flag.Red, state.Flag);
        }
    }

    /// <summary>
    /// The raw-extraction path: fakes mirror the researched
    /// <c>DataSampleEx</c> shape (docs/simhub-plugin-api.md) — a
    /// <c>Telemetry</c> property whose value is a
    /// <c>Dictionary&lt;string, object&gt;</c> subclass, with
    /// <c>"SessionFlags"</c> boxed as <c>int</c> like the real reader.
    /// </summary>
    public class IRacingRawExtractionTests
    {
        private sealed class FakeTelemetryDictionary : Dictionary<string, object>
        {
        }

        private sealed class FakeDataSample
        {
            public FakeTelemetryDictionary Telemetry { get; set; }
        }

        /// <summary>A raw object with a Telemetry property of a useless shape.</summary>
        private sealed class WrongShapeSample
        {
            public string Telemetry => "not a dictionary";
        }

        private class ShadowedBaseSample
        {
            public FakeTelemetryDictionary Telemetry { get; set; }
        }

        /// <summary>
        /// Hides the base <c>Telemetry</c> with a different property type —
        /// the one drift shape where <c>Type.GetProperty("Telemetry")</c>
        /// throws <c>AmbiguousMatchException</c> instead of returning null
        /// (same-type shadowing resolves fine; changed-type shadowing does
        /// not).
        /// </summary>
        private sealed class ShadowingSample : ShadowedBaseSample
        {
            public new string Telemetry => "shadowed";
        }

        private sealed class RawStatusData : StatusDataBase
        {
            private readonly object _raw;

            public RawStatusData(object raw)
            {
                _raw = raw;
            }

            public override object GetRawDataObject() => _raw;
        }

        private static TelemetrySnapshot Extract(object raw, string gameName = "IRacing")
        {
            var data = new GameData();
            Set(data, nameof(GameData.GameRunning), true);
            Set(data, nameof(GameData.GameName), gameName);
            data.NewData = new RawStatusData(raw);
            var snapshot = new TelemetrySnapshot();
            GameDataExtractor.Extract(ref data, snapshot);
            return snapshot;
        }

        private static void Set(object target, string property, object value)
        {
            System.Reflection.PropertyInfo info = target.GetType().GetProperty(property);
            Assert.NotNull(info);
            info.SetMethod.Invoke(target, new[] { value });
        }

        private static FakeDataSample Sample(object sessionFlags)
        {
            var telemetry = new FakeTelemetryDictionary();
            if (sessionFlags != null)
            {
                telemetry["SessionFlags"] = sessionFlags;
            }
            return new FakeDataSample { Telemetry = telemetry };
        }

        [Fact]
        public void ReadsTheBoxedIntSessionFlags()
        {
            TelemetrySnapshot snapshot = Extract(Sample(0x4108)); // yellow|yellowWaving|caution
            Assert.True(snapshot.HasRawSessionFlags);
            Assert.Equal(0x4108u, snapshot.RawSessionFlags);
        }

        [Fact]
        public void NegativeIntPreservesTheHighBits()
        {
            // startGo = 0x80000000 makes the boxed int negative; the
            // unchecked reinterpretation must keep the bit pattern.
            TelemetrySnapshot snapshot = Extract(Sample(unchecked((int)0x80004000)));
            Assert.True(snapshot.HasRawSessionFlags);
            Assert.Equal(0x80004000u, snapshot.RawSessionFlags);
        }

        [Fact]
        public void NonIRacingGamesNeverTouchRawData()
        {
            TelemetrySnapshot snapshot = Extract(Sample(0x10), gameName: "Ac");
            Assert.False(snapshot.HasRawSessionFlags);
            Assert.Equal(0u, snapshot.RawSessionFlags);
        }

        [Fact]
        public void NullRawObjectIsInvalidNotFatal()
        {
            TelemetrySnapshot snapshot = Extract(null);
            Assert.False(snapshot.HasRawSessionFlags);
        }

        [Fact]
        public void MissingSessionFlagsKeyIsInvalidNotFatal()
        {
            TelemetrySnapshot snapshot = Extract(Sample(null));
            Assert.False(snapshot.HasRawSessionFlags);
        }

        [Fact]
        public void UnexpectedTelemetryShapeIsInvalidNotFatal()
        {
            TelemetrySnapshot snapshot = Extract(new WrongShapeSample());
            Assert.False(snapshot.HasRawSessionFlags);
        }

        [Fact]
        public void UnexpectedBoxedTypeIsInvalidNotFatal()
        {
            TelemetrySnapshot snapshot = Extract(Sample("0x10"));
            Assert.False(snapshot.HasRawSessionFlags);
        }

        [Fact]
        public void AmbiguousTelemetryPropertyIsInvalidNotFatal()
        {
            // Fixture sanity: prove this shape really takes the throwing
            // path (GetProperty throws AmbiguousMatchException instead of
            // returning null) so the assertion below exercises the guard.
            Assert.Throws<System.Reflection.AmbiguousMatchException>(
                () => typeof(ShadowingSample).GetProperty("Telemetry"));

            // The extractor must swallow it and degrade to "no raw data" —
            // an exception escaping DataUpdate gets the plugin throttled
            // by SimHub.
            TelemetrySnapshot snapshot = Extract(new ShadowingSample());
            Assert.False(snapshot.HasRawSessionFlags);
        }

        [Fact]
        public void RawTypeChangeReResolvesTheCachedProperty()
        {
            // The extractor caches (Type, PropertyInfo); alternate two raw
            // shapes to prove the cache re-keys instead of misreading.
            TelemetrySnapshot good = Extract(Sample(0x08));
            Assert.True(good.HasRawSessionFlags);
            TelemetrySnapshot wrong = Extract(new WrongShapeSample());
            Assert.False(wrong.HasRawSessionFlags);
            TelemetrySnapshot goodAgain = Extract(Sample(0x10));
            Assert.True(goodAgain.HasRawSessionFlags);
            Assert.Equal(0x10u, goodAgain.RawSessionFlags);
        }

        [Fact]
        public void StaleRawFlagsNeverLeakThroughAClearedTick()
        {
            var snapshot = new TelemetrySnapshot
            {
                HasRawSessionFlags = true,
                RawSessionFlags = 0x4000,
            };
            GameData data = null;
            GameDataExtractor.Extract(ref data, snapshot);
            Assert.False(snapshot.HasRawSessionFlags);
            Assert.Equal(0u, snapshot.RawSessionFlags);
        }
    }
}
