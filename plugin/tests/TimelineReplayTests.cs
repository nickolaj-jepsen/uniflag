// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter integration tests: replay the two transcribed scenario timelines
// under testdata/timelines/ through the adapter pipeline, and replay
// SYNTHETIC iRacing SessionFlags sequences through the generic+iRacing
// layers.
//
// The timeline fixtures are game-agnostic RenderState scripts, so they run
// through the GENERIC pipeline only: each step is projected onto a
// telemetry snapshot (unified flags + a representative SessionTypeName)
// and the expected output is the step folded through the documented
// generic-adapter capability limits (docs/simhub-flag-properties.md,
// "Generic adapter mapping"): the unified layer has no red flag, no
// caution, no sectors, no wave levels (yellow always guesses Single), and
// SessionTypeName keeps reading "Race" after the chequered flag, so
// PostRace projects to Racing.
//
// The iRacing sequences are SYNTHETIC: they are derived from the
// researched irsdk_Flags bit layout (verified against the iRacingSDK.dll
// shipped inside SimHub 9.11.21), NOT captured from a live session — a
// captured-telemetry/live-session pass is on the maintainer's checklist.

using System;
using System.Collections.Generic;
using System.IO;
using Uniflag.Adapters;
using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class TimelineReplayTests
    {
        /// <summary>One parsed timeline step (delay is checked but not slept on).</summary>
        private sealed class Step
        {
            public long DelayMs;
            public Flag Flag;
            public WaveLevel Wave;
            public Session Session;
            public Caution Caution;
            public SectorSet Sectors;
        }

        private static string TimelinesDir =>
            Path.Combine(RepoPaths.RepoRoot, "testdata", "timelines");

        private static List<Step> LoadTimeline(string fileName, out string description)
        {
            string path = Path.Combine(TimelinesDir, fileName);
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(path));
            Assert.Equal(1L, (long)root["format"]);
            description = (string)root["description"];
            var steps = new List<Step>();
            foreach (object stepObj in (List<object>)root["steps"])
            {
                var entry = (Dictionary<string, object>)stepObj;
                SectorSet sectors = SectorSet.Empty;
                foreach (object sector in (List<object>)entry["sectors"])
                {
                    sectors = sectors.With((int)(long)sector);
                }
                steps.Add(new Step
                {
                    DelayMs = (long)entry["delay_ms"],
                    Flag = (Flag)Enum.Parse(typeof(Flag), (string)entry["flag"]),
                    Wave = (WaveLevel)Enum.Parse(typeof(WaveLevel), (string)entry["wave"]),
                    Session = (Session)Enum.Parse(typeof(Session), (string)entry["session"]),
                    Caution = (Caution)Enum.Parse(typeof(Caution), (string)entry["caution"]),
                    Sectors = sectors,
                });
            }
            return steps;
        }

        /// <summary>
        /// Project a fixture step onto the unified telemetry a generic game
        /// would report: one Flag_* boolean for the step's flag (red has no
        /// unified representation — nothing is set), plus a representative
        /// SessionTypeName per session vocabulary word.
        /// </summary>
        private static TelemetrySnapshot SnapshotOf(Step step)
        {
            var snapshot = new TelemetrySnapshot
            {
                GameRunning = true,
                GameInMenu = false,
                GamePaused = step.Session == Session.Paused,
                HasData = true,
                GameName = "GenericGame",
                SessionTypeName = SessionName(step.Session),
            };
            switch (step.Flag)
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
                default:
                    // None — and Red, which the unified layer cannot carry.
                    break;
            }
            return snapshot;
        }

        private static string SessionName(Session session)
        {
            switch (session)
            {
                case Session.PreRace:
                    return "Practice";
                case Session.Racing:
                case Session.Paused: // paused is carried by GamePaused, not the name
                case Session.PostRace: // games keep reporting "Race" post-chequered
                    return "Race";
                default:
                    return "Garage"; // maps to Unknown
            }
        }

        /// <summary>
        /// The step folded through the generic layer's documented limits:
        /// red → None (unified never carries it), yellow → Single wave
        /// (the heuristic), all other waves → None, caution/sectors →
        /// none/empty, PostRace → Racing (SessionTypeName still "Race").
        /// </summary>
        private static RenderState ExpectedGenericProjection(Step step)
        {
            RenderState expected = RenderState.Default;
            expected.Flag = step.Flag == Flag.Red ? Flag.None : step.Flag;
            expected.Wave = expected.Flag == Flag.Yellow ? WaveLevel.Single : WaveLevel.None;
            expected.Session = step.Session == Session.PostRace ? Session.Racing : step.Session;
            expected.Caution = Caution.None;
            expected.Sectors = SectorSet.Empty;
            return expected;
        }

        private static void AssertStatesEqual(RenderState want, RenderState got, string context)
        {
            Assert.True(want.Flag == got.Flag, $"{context}: flag {got.Flag}, want {want.Flag}");
            Assert.True(want.Wave == got.Wave, $"{context}: wave {got.Wave}, want {want.Wave}");
            Assert.True(want.Session == got.Session, $"{context}: session {got.Session}, want {want.Session}");
            Assert.True(want.Caution == got.Caution, $"{context}: caution {got.Caution}, want {want.Caution}");
            Assert.True(want.Sectors.Equals(got.Sectors), $"{context}: sector masks differ");
            Assert.True(want.Slowdown == got.Slowdown, $"{context}: slowdown {got.Slowdown}, want {want.Slowdown}");
            Assert.True(want.Meatball == got.Meatball, $"{context}: meatball {got.Meatball}, want {want.Meatball}");
            Assert.True(
                want.BlackDetail == got.BlackDetail,
                $"{context}: black detail {got.BlackDetail}, want {want.BlackDetail}");
            Assert.True(want.Furled == got.Furled, $"{context}: furled {got.Furled}, want {want.Furled}");
        }

        [Theory]
        [InlineData("race-arc.json", 10)]
        [InlineData("caution-and-sectors.json", 11)]
        public void TimelineRepliesThroughTheGenericPipelinePerStep(string fileName, int wantSteps)
        {
            List<Step> steps = LoadTimeline(fileName, out string description);
            Assert.False(string.IsNullOrEmpty(description));
            Assert.Equal(wantSteps, steps.Count);

            // The full production pipeline: generic + the iRacing refiner,
            // which must stay dormant for a non-iRacing game name.
            var pipeline = new AdapterPipeline(new IRacingAdapter());
            for (int i = 0; i < steps.Count; i++)
            {
                Step step = steps[i];
                Assert.True(step.DelayMs >= 0, $"step {i}: negative delay");
                RenderState got = pipeline.Map(SnapshotOf(step));
                AssertStatesEqual(ExpectedGenericProjection(step), got, $"{fileName} step {i}");
            }
        }

        [Fact]
        public void TimelineStepsWithDirectUnifiedEquivalentsRoundTripExactly()
        {
            // Sanity on the projection itself: every step whose fixture
            // state IS reachable through the unified layer (non-red flag,
            // yellow-single-or-none wave, no caution/sectors, session in
            // the generic vocabulary) must round-trip identically — the
            // projection only ever bends the documented limit cases.
            foreach (string fileName in new[] { "race-arc.json", "caution-and-sectors.json" })
            {
                List<Step> steps = LoadTimeline(fileName, out _);
                var pipeline = new AdapterPipeline(new IRacingAdapter());
                int exact = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    Step step = steps[i];
                    bool reachable = step.Flag != Flag.Red
                        && step.Caution == Caution.None
                        && step.Sectors.IsEmpty
                        && (step.Flag == Flag.Yellow
                            ? step.Wave == WaveLevel.Single
                            : step.Wave == WaveLevel.None)
                        && step.Session != Session.PostRace;
                    if (!reachable)
                    {
                        continue;
                    }
                    RenderState got = pipeline.Map(SnapshotOf(step));
                    Assert.True(step.Flag == got.Flag && step.Wave == got.Wave
                        && step.Session == got.Session,
                        $"{fileName} step {i}: reachable step did not round-trip");
                    exact++;
                }
                Assert.True(exact > 0, $"{fileName}: no reachable steps — projection test is vacuous");
            }
        }
    }

    /// <summary>
    /// SYNTHETIC iRacing sequences through generic+iRacing. Masks are
    /// hand-built from the verified irsdk_Flags bit layout — synthetic, not
    /// captured telemetry.
    /// </summary>
    public class IRacingSyntheticSequenceTests
    {
        private sealed class Expect
        {
            public Expect(uint mask, Flag flag, WaveLevel wave, Caution caution,
                bool meatball = false, bool furled = false, string because = null)
            {
                Mask = mask;
                Flag = flag;
                Wave = wave;
                Caution = caution;
                Meatball = meatball;
                Furled = furled;
                Because = because ?? string.Empty;
            }

            public uint Mask { get; }
            public Flag Flag { get; }
            public WaveLevel Wave { get; }
            public Caution Caution { get; }
            public bool Meatball { get; }
            public bool Furled { get; }
            public string Because { get; }
        }

        [Fact]
        public void RaceArcWithCautionAndPenaltiesMapsPerStep()
        {
            // A plausible race arc: start held → green → local yellow
            // (displayed, then waved) → full-course caution → restart →
            // warning → repair order → black flag → red → finish. Each
            // mask is synthetic (built from the verified bit values).
            Expect[] sequence =
            {
                new Expect(IRacingAdapter.FlagGreenHeld, Flag.Green, WaveLevel.None, Caution.None,
                    because: "greenHeld alone: raw-only green the unified layer drops"),
                new Expect(IRacingAdapter.FlagGreen, Flag.Green, WaveLevel.None, Caution.None),
                new Expect(0, Flag.None, WaveLevel.None, Caution.None),
                new Expect(IRacingAdapter.FlagYellow, Flag.Yellow, WaveLevel.None, Caution.None,
                    because: "displayed yellow: raw kills the generic Single guess"),
                new Expect(IRacingAdapter.FlagYellow | IRacingAdapter.FlagYellowWaving,
                    Flag.Yellow, WaveLevel.Single, Caution.None),
                new Expect(IRacingAdapter.FlagCaution | IRacingAdapter.FlagCautionWaving
                        | IRacingAdapter.FlagYellowWaving,
                    Flag.Yellow, WaveLevel.Single, Caution.SafetyCar,
                    because: "full-course caution deploys the SC board over the yellow base"),
                new Expect(IRacingAdapter.FlagOneLapToGreen | IRacingAdapter.FlagCaution,
                    Flag.Yellow, WaveLevel.None, Caution.SafetyCar,
                    because: "one-to-green: caution still up, no waving bits left"),
                new Expect(0, Flag.None, WaveLevel.None, Caution.None),
                new Expect(IRacingAdapter.FlagFurled, Flag.None, WaveLevel.None, Caution.None,
                    furled: true),
                new Expect(IRacingAdapter.FlagRepair, Flag.Orange, WaveLevel.None, Caution.None,
                    meatball: true,
                    because: "repair: unified orange + the meatball board"),
                new Expect(IRacingAdapter.FlagBlack, Flag.Black, WaveLevel.None, Caution.None),
                new Expect(IRacingAdapter.FlagRed, Flag.Red, WaveLevel.None, Caution.None),
                new Expect(IRacingAdapter.FlagWhite, Flag.White, WaveLevel.None, Caution.None),
                new Expect(IRacingAdapter.FlagCheckered, Flag.Checkered, WaveLevel.None, Caution.None),
            };

            var pipeline = new AdapterPipeline(new IRacingAdapter());
            for (int i = 0; i < sequence.Length; i++)
            {
                Expect expect = sequence[i];
                RenderState got = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(expect.Mask));
                string context = $"step {i} (mask 0x{expect.Mask:X}) {expect.Because}";
                Assert.True(expect.Flag == got.Flag, $"{context}: flag {got.Flag}, want {expect.Flag}");
                Assert.True(expect.Wave == got.Wave, $"{context}: wave {got.Wave}, want {expect.Wave}");
                Assert.True(expect.Caution == got.Caution,
                    $"{context}: caution {got.Caution}, want {expect.Caution}");
                Assert.True(expect.Meatball == got.Meatball,
                    $"{context}: meatball {got.Meatball}, want {expect.Meatball}");
                Assert.True(expect.Furled == got.Furled,
                    $"{context}: furled {got.Furled}, want {expect.Furled}");
                // Never fabricated from iRacing telemetry:
                Assert.Equal(0, got.Slowdown);
                Assert.Equal(BlackFlagDetail.None, got.BlackDetail);
            }
        }

        [Fact]
        public void RedDuringCautionRendersRedAndSuppressesTheBoard()
        {
            // Red + caution simultaneously: the adapter reports both; the
            // precedence ladder (not the adapter) resolves red on top —
            // mirroring the caution-and-sectors fixture's red-over-VSC step.
            var pipeline = new AdapterPipeline(new IRacingAdapter());
            RenderState state = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(
                IRacingAdapter.FlagRed | IRacingAdapter.FlagCaution));
            Assert.Equal(Flag.Red, state.Flag);
            Assert.Equal(Caution.SafetyCar, state.Caution);
            Assert.Equal(RenderLayer.RedFlag, Precedence.Select(state, connected: true));
            Assert.False(Precedence.SectorBandVisible(state, connected: true));
            Assert.False(Precedence.FurledAccentVisible(state, connected: true));
        }

        [Fact]
        public void PenaltyBitsComposeWithTheCautionLadder()
        {
            // Meatball + furled during a caution: adapter carries all three
            // dimensions; the board still wins the base layer and the
            // accent overlays it.
            var pipeline = new AdapterPipeline(new IRacingAdapter());
            RenderState state = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(
                IRacingAdapter.FlagCaution | IRacingAdapter.FlagRepair | IRacingAdapter.FlagFurled));
            Assert.Equal(Caution.SafetyCar, state.Caution);
            Assert.True(state.Meatball);
            Assert.True(state.Furled);
            Assert.Equal(RenderLayer.SafetyCarBoard, Precedence.Select(state, connected: true));
            Assert.True(Precedence.FurledAccentVisible(state, connected: true));
        }
    }
}
