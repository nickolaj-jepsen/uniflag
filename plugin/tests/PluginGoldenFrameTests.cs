// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Golden-frame suite for the C#-AUTHORED corpus at testdata/frames-plugin/
// — the penalty effects pinned byte-exactly, in the same style as
// GoldenFrameTests: iterate the manifest, render each
// (state, frame, flag_age, connected) tuple, compare all 3072 bytes. Two
// corpus-policy differences from the ported-parity suite: this corpus IS
// regenerable (via PluginGoldenDumper, the [windows] leg of
// `just golden-regen`) because its baselines are pending maintainer visual
// review, and the manifest must stay in lockstep with the in-code ledger —
// ManifestMatchesTheScenarioTable fails the build the moment the table
// changes without a deliberate regen commit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Uniflag.Rendering;
using Xunit;
using Xunit.Sdk;

namespace Uniflag.Tests
{
    public class PluginGoldenFrameTests
    {
        /// <summary>One manifest entry: everything Paint needs plus the fixture file.</summary>
        private sealed class GoldenScenario
        {
            public GoldenScenario(
                string name, string file, RenderState state, uint frame, uint flagAge,
                bool connected, string description)
            {
                Name = name;
                File = file;
                State = state;
                Frame = frame;
                FlagAge = flagAge;
                Connected = connected;
                Description = description;
            }

            public string Name { get; }
            public string File { get; }
            public RenderState State { get; }
            public uint Frame { get; }
            public uint FlagAge { get; }
            public bool Connected { get; }
            public string Description { get; }
        }

        private static string CorpusDir => PluginGoldenScenarios.CorpusDir;

        private static readonly Lazy<List<GoldenScenario>> Scenarios =
            new Lazy<List<GoldenScenario>>(LoadManifest);

        private static List<GoldenScenario> LoadManifest()
        {
            string path = Path.Combine(CorpusDir, "manifest.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"missing plugin golden manifest {path}; regenerate via just golden-regen (windows leg)",
                    path);
            }
            var root = (List<object>)MiniJson.Parse(File.ReadAllText(path));
            var scenarios = new List<GoldenScenario>();
            var seen = new HashSet<string>();
            foreach (object entryObj in root)
            {
                var entry = (Dictionary<string, object>)entryObj;
                var stateObj = (Dictionary<string, object>)entry["state"];
                SectorSet sectors = SectorSet.Empty;
                foreach (object sector in (List<object>)stateObj["sectors"])
                {
                    sectors = sectors.With((int)(long)sector);
                }
                var state = new RenderState
                {
                    Flag = ParseEnum<Flag>(stateObj["flag"]),
                    Wave = ParseEnum<WaveLevel>(stateObj["wave"]),
                    Session = ParseEnum<Session>(stateObj["session"]),
                    Caution = ParseEnum<Caution>(stateObj["caution"]),
                    Sectors = sectors,
                    // Penalty extension fields — required in this corpus's
                    // manifest (unlike the frozen frames manifest, which
                    // predates them and never carries penalty state).
                    Slowdown = checked((byte)(long)stateObj["slowdown"]),
                    Meatball = (bool)stateObj["meatball"],
                    BlackDetail = ParseEnum<BlackFlagDetail>(stateObj["black_detail"]),
                    Furled = (bool)stateObj["furled"],
                };
                var scenario = new GoldenScenario(
                    (string)entry["name"],
                    (string)entry["file"],
                    state,
                    checked((uint)(long)entry["frame"]),
                    checked((uint)(long)entry["flag_age"]),
                    (bool)entry["connected"],
                    (string)entry["description"]);
                if (!seen.Add(scenario.Name))
                {
                    throw new InvalidDataException($"duplicate scenario name '{scenario.Name}' in manifest");
                }
                scenarios.Add(scenario);
            }
            return scenarios;
        }

        private static T ParseEnum<T>(object value) where T : struct
        {
            // Strict parse, mirroring GoldenFrameTests: numeric strings and
            // undefined values must fail loudly at load time.
            var parsed = (T)Enum.Parse(typeof(T), (string)value, ignoreCase: false);
            if (!Enum.IsDefined(typeof(T), parsed))
            {
                throw new InvalidDataException(
                    $"manifest value '{value}' is not a defined {typeof(T).Name}");
            }
            return parsed;
        }

        private static GoldenScenario Find(string name)
        {
            foreach (GoldenScenario scenario in Scenarios.Value)
            {
                if (scenario.Name == name)
                {
                    return scenario;
                }
            }
            throw new KeyNotFoundException($"scenario '{name}' not in manifest");
        }

        public static TheoryData<string> ScenarioNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (GoldenScenario scenario in Scenarios.Value)
                {
                    data.Add(scenario.Name);
                }
                return data;
            }
        }

        [Fact]
        public void ManifestPinsTheWholeCorpus()
        {
            // The in-code ledger holds 20 entries; append-only, so fewer
            // means a broken checkout or a missed regen.
            Assert.Equal(20, Scenarios.Value.Count);
            foreach (GoldenScenario scenario in Scenarios.Value)
            {
                Assert.True(
                    File.Exists(Path.Combine(CorpusDir, scenario.File)),
                    $"manifest entry '{scenario.Name}' references missing file {scenario.File}");
                Assert.Contains(PluginGoldenScenarios.ReviewMarker, scenario.Description);
            }
        }

        [Fact]
        public void ManifestMatchesTheScenarioTable()
        {
            // Regen discipline: the on-disk manifest must be exactly what
            // the dumper would write from the current ledger. A table edit
            // without `just golden-regen` (windows leg) fails here instead
            // of silently testing stale tuples.
            string onDisk = File.ReadAllText(
                Path.Combine(CorpusDir, "manifest.json"),
                Encoding.UTF8);
            Assert.Equal(PluginGoldenScenarios.BuildManifestJson(), onDisk);
        }

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void RenderedFrameMatchesGoldenBytes(string name)
        {
            GoldenScenario scenario = Find(name);
            byte[] want = File.ReadAllBytes(Path.Combine(CorpusDir, scenario.File));
            Assert.True(
                want.Length == FrameBuffer.ByteLength,
                $"{scenario.File}: golden file is {want.Length} bytes, expected {FrameBuffer.ByteLength}");

            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, scenario.State, scenario.Frame, scenario.FlagAge, scenario.Connected);
            AssertFrameEqual(name, frameBuffer.Pixels, want);
        }

        /// <summary>
        /// Byte-exact frame comparison with pixel-level diagnostics —
        /// identical to the ported-parity suite's assert.
        /// </summary>
        private static void AssertFrameEqual(string name, byte[] got, byte[] want)
        {
            int firstBadPixel = -1;
            int badPixels = 0;
            for (int p = 0; p < FrameBuffer.Width * FrameBuffer.Height; p++)
            {
                int i = p * 3;
                if (got[i] != want[i] || got[i + 1] != want[i + 1] || got[i + 2] != want[i + 2])
                {
                    badPixels++;
                    if (firstBadPixel < 0)
                    {
                        firstBadPixel = p;
                    }
                }
            }
            if (firstBadPixel < 0)
            {
                return;
            }
            int x = firstBadPixel % FrameBuffer.Width;
            int y = firstBadPixel / FrameBuffer.Width;
            int o = firstBadPixel * 3;
            throw new XunitException(
                $"{name}: frame mismatch — first differing pixel at ({x},{y}): " +
                $"got ({got[o]},{got[o + 1]},{got[o + 2]}), want ({want[o]},{want[o + 1]},{want[o + 2]}); " +
                $"{badPixels} of {FrameBuffer.Width * FrameBuffer.Height} pixels differ");
        }
    }
}
