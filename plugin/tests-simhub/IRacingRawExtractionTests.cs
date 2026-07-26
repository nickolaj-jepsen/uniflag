// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The raw-extraction path through StatusDataBase.GetRawDataObject(),
// against fakes mirroring the real IRacingReader.DataSampleEx shape (a
// `Telemetry` property whose value derives from Dictionary<string, object>).
// The SessionFlags mapping rules live in
// plugin/tests-core/IRacingAdapterTests.cs.

using System.Collections.Generic;
using GameReaderCommon;
using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
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

        // --- Incident count + limit extraction. Fakes mirror the researched
        //     DataSampleEx shape: a `SessionDataDict` (Dictionary<string,
        //     object>) holding the raw WeekendInfo → WeekendOptions →
        //     IncidentLimit tree the typed SessionData model omits, plus
        //     PlayerCarMyIncidentCount in the Telemetry dictionary.

        private sealed class FakeFullSample
        {
            public FakeTelemetryDictionary Telemetry { get; set; }

            public Dictionary<string, object> SessionDataDict { get; set; }
        }

        private static FakeFullSample FullSample(
            object incidentCount, object incidentLimit, bool includeLimitKey = true)
        {
            var telemetry = new FakeTelemetryDictionary();
            if (incidentCount != null)
            {
                telemetry["PlayerCarMyIncidentCount"] = incidentCount;
            }
            var weekendOptions = new Dictionary<string, object>();
            if (includeLimitKey)
            {
                weekendOptions["IncidentLimit"] = incidentLimit;
            }
            var sessionData = new Dictionary<string, object>
            {
                ["WeekendInfo"] = new Dictionary<string, object> { ["WeekendOptions"] = weekendOptions },
            };
            return new FakeFullSample { Telemetry = telemetry, SessionDataDict = sessionData };
        }

        [Fact]
        public void ReadsThePlayerIncidentCount()
        {
            TelemetrySnapshot snapshot = Extract(FullSample(incidentCount: 7, incidentLimit: 17L));
            Assert.True(snapshot.HasIncidentCount);
            Assert.Equal(7, snapshot.IncidentCount);
        }

        [Fact]
        public void ReadsTheNestedIncidentLimit()
        {
            // Numeric IncidentLimit boxed as Int64 (iRacingSDK parses numeric
            // YAML scalars to long, like the typed WeekendOptions fields).
            TelemetrySnapshot snapshot = Extract(FullSample(incidentCount: 3, incidentLimit: 17L));
            Assert.True(snapshot.HasIncidentLimit);
            Assert.Equal(17, snapshot.IncidentLimit);
        }

        [Fact]
        public void ParsesANumericStringIncidentLimit()
        {
            TelemetrySnapshot snapshot = Extract(FullSample(incidentCount: 0, incidentLimit: "8"));
            Assert.True(snapshot.HasIncidentLimit);
            Assert.Equal(8, snapshot.IncidentLimit);
        }

        [Fact]
        public void UnlimitedIncidentLimitIsNoFiniteLimit()
        {
            TelemetrySnapshot snapshot = Extract(FullSample(incidentCount: 0, incidentLimit: "unlimited"));
            Assert.False(snapshot.HasIncidentLimit);
        }

        [Fact]
        public void MissingSessionDataDictLeavesTheLimitUnknown()
        {
            // The telemetry-only fake exposes no SessionDataDict property.
            TelemetrySnapshot snapshot = Extract(Sample(0x08));
            Assert.False(snapshot.HasIncidentLimit);
        }

        [Fact]
        public void MissingIncidentLimitKeyLeavesItUnknown()
        {
            TelemetrySnapshot snapshot = Extract(
                FullSample(incidentCount: 2, incidentLimit: null, includeLimitKey: false));
            Assert.True(snapshot.HasIncidentCount);
            Assert.False(snapshot.HasIncidentLimit);
        }

        [Fact]
        public void NonIRacingGameNeverReadsIncidentData()
        {
            TelemetrySnapshot snapshot = Extract(
                FullSample(incidentCount: 5, incidentLimit: 17L), gameName: "Ac");
            Assert.False(snapshot.HasIncidentCount);
            Assert.False(snapshot.HasIncidentLimit);
        }
    }
}
