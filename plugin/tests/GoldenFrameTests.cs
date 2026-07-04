// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Cross-language golden-frame conformance suite (docs/effects-spec.md §8,
// docs/v2-plan.md M3 step 8): iterate testdata/frames/manifest.json, render
// each pinned (state, frame, flag_age, connected) tuple through the C# port
// of effects::paint, and byte-compare all 3072 bytes against the frozen
// .rgb dump. No tolerance, no epsilon — the fixtures are the contract and
// are never regenerated from this side.

using System;
using System.Collections.Generic;
using System.IO;
using Uniflag.Rendering;
using Xunit;
using Xunit.Sdk;

namespace Uniflag.Tests
{
    public class GoldenFrameTests
    {
        /// <summary>One manifest entry: everything Paint needs plus the fixture file.</summary>
        private sealed class GoldenScenario
        {
            public GoldenScenario(string name, string file, RenderState state, uint frame, uint flagAge, bool connected)
            {
                Name = name;
                File = file;
                State = state;
                Frame = frame;
                FlagAge = flagAge;
                Connected = connected;
            }

            public string Name { get; }
            public string File { get; }
            public RenderState State { get; }
            public uint Frame { get; }
            public uint FlagAge { get; }
            public bool Connected { get; }
        }

        private static string FramesDir => Path.Combine(RepoPaths.RepoRoot, "testdata", "frames");

        private static readonly Lazy<List<GoldenScenario>> Scenarios =
            new Lazy<List<GoldenScenario>>(LoadManifest);

        private static List<GoldenScenario> LoadManifest()
        {
            string path = Path.Combine(FramesDir, "manifest.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"missing golden manifest {path}", path);
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
                };
                var scenario = new GoldenScenario(
                    (string)entry["name"],
                    (string)entry["file"],
                    state,
                    checked((uint)(long)entry["frame"]),
                    checked((uint)(long)entry["flag_age"]),
                    (bool)entry["connected"]);
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
            // Enum.Parse silently accepts numeric strings ("9", "-1") and
            // comma lists, yielding undefined values — a corrupt manifest
            // must fail loudly at load time instead.
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
        public void ManifestPinsTheFullFrozenCorpus()
        {
            // The corpus froze permanently at M11 with exactly 40 entries
            // (the manifest is the ledger; the Rust dumper is gone), so any
            // other count means a broken checkout.
            Assert.Equal(40, Scenarios.Value.Count);
            foreach (GoldenScenario scenario in Scenarios.Value)
            {
                Assert.True(
                    File.Exists(Path.Combine(FramesDir, scenario.File)),
                    $"manifest entry '{scenario.Name}' references missing file {scenario.File}");
            }
        }

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void RenderedFrameMatchesGoldenBytes(string name)
        {
            GoldenScenario scenario = Find(name);
            byte[] want = File.ReadAllBytes(Path.Combine(FramesDir, scenario.File));
            Assert.True(
                want.Length == FrameBuffer.ByteLength,
                $"{scenario.File}: golden file is {want.Length} bytes, expected {FrameBuffer.ByteLength}");

            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, scenario.State, scenario.Frame, scenario.FlagAge, scenario.Connected);
            AssertFrameEqual(name, frameBuffer.Pixels, want);
        }

        /// <summary>
        /// Byte-exact frame comparison with pixel-level diagnostics: reports
        /// the first differing pixel as (x, y, got RGB, want RGB) plus the
        /// total number of differing pixels.
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
