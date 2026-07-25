// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter integration tests: replay the two transcribed scenario timelines
// under testdata/timelines/ through the adapter pipeline, and replay
// SYNTHETIC iRacing SessionFlags sequences through the generic+iRacing
// layers.
//
// The timeline fixtures are game-agnostic SignalState scripts (format 2), so
// they run through the GENERIC pipeline only: each step is projected onto a
// telemetry snapshot (unified flags + a representative SessionTypeName)
// and the expected output is the step folded through the documented
// generic-adapter capability limits (docs/simhub-flag-properties.md,
// "Generic adapter mapping"): the unified layer has no red flag, no
// caution, no sectors, no urgency detail (yellow always guesses Alert),
// and SessionTypeName keeps reading "Race" after the chequered flag, so
// PostRace projects to Racing.
//
// The iRacing sequences are SYNTHETIC: they are derived from the
// researched irsdk_Flags bit layout (verified against the iRacingSDK.dll
// shipped inside SimHub 9.11.21), NOT captured from a live session — a
// captured-telemetry/live-session pass is on the maintainer's checklist.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Uniflag.Adapters;
using Uniflag.Rendering.Grammar;
using Xunit;
using SectorSet = Uniflag.Rendering.SectorSet;
using Session = Uniflag.Rendering.Session;

namespace Uniflag.Tests
{
    public class TimelineReplayTests
    {
        /// <summary>One parsed timeline step (delay is checked but not slept on).</summary>
        private sealed class Step
        {
            public long DelayMs;
            public TrackFlag Flag;
            public Tier Tier;
            public Session Session;
            public Caution Caution;
            public SectorSet Sectors;
        }

        private static string TimelinesDir =>
            Path.Combine(RepoPaths.RepoRoot, "testdata", "timelines");

        private static List<Step> LoadTimeline(string fileName, out string description)
        {
            string path = Path.Combine(TimelinesDir, fileName);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            Assert.Equal(2, root.GetProperty("format").GetInt32());
            description = root.GetProperty("description").GetString();
            var steps = new List<Step>();
            foreach (JsonElement entry in root.GetProperty("steps").EnumerateArray())
            {
                SectorSet sectors = SectorSet.Empty;
                foreach (JsonElement sector in entry.GetProperty("sectors").EnumerateArray())
                {
                    sectors = sectors.With(sector.GetInt32());
                }
                steps.Add(new Step
                {
                    DelayMs = entry.GetProperty("delay_ms").GetInt64(),
                    Flag = Enum.Parse<TrackFlag>(entry.GetProperty("flag").GetString()),
                    Tier = Enum.Parse<Tier>(entry.GetProperty("tier").GetString()),
                    Session = Enum.Parse<Session>(entry.GetProperty("session").GetString()),
                    Caution = Enum.Parse<Caution>(entry.GetProperty("caution").GetString()),
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
        /// red → None (unified never carries it), yellow → Alert tier (the
        /// heuristic), all other flags → Ambient, caution/sectors →
        /// none/empty, PostRace → Racing (SessionTypeName still "Race").
        /// </summary>
        private static SignalState ExpectedGenericProjection(Step step)
        {
            SignalState expected = SignalState.Default;
            expected.Flag = step.Flag == TrackFlag.Red ? TrackFlag.None : step.Flag;
            expected.Tier = expected.Flag == TrackFlag.Yellow ? Tier.Alert : Tier.Ambient;
            expected.Session = step.Session == Session.PostRace ? Session.Racing : step.Session;
            expected.Caution = Caution.None;
            expected.Sectors = SectorSet.Empty;
            return expected;
        }

        private static void AssertStatesEqual(SignalState want, SignalState got, string context)
        {
            Assert.True(want.Flag == got.Flag, $"{context}: flag {got.Flag}, want {want.Flag}");
            Assert.True(want.Tier == got.Tier, $"{context}: tier {got.Tier}, want {want.Tier}");
            Assert.True(want.Session == got.Session, $"{context}: session {got.Session}, want {want.Session}");
            Assert.True(want.Caution == got.Caution, $"{context}: caution {got.Caution}, want {want.Caution}");
            Assert.True(want.Sectors.Equals(got.Sectors), $"{context}: sector masks differ");
            Assert.True(want.BlackFlag == got.BlackFlag, $"{context}: black {got.BlackFlag}, want {want.BlackFlag}");
            Assert.True(
                want.BlackDetail == got.BlackDetail,
                $"{context}: black detail {got.BlackDetail}, want {want.BlackDetail}");
            Assert.True(want.Meatball == got.Meatball, $"{context}: meatball {got.Meatball}, want {want.Meatball}");
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
                SignalState got = pipeline.Map(SnapshotOf(step));
                AssertStatesEqual(ExpectedGenericProjection(step), got, $"{fileName} step {i}");
            }
        }

        [Fact]
        public void TimelineStepsWithDirectUnifiedEquivalentsRoundTripExactly()
        {
            // Sanity on the projection itself: every step whose fixture
            // state IS reachable through the unified layer (non-red flag,
            // the yellow-Alert-else-Ambient tier, no caution/sectors,
            // session in the generic vocabulary) must round-trip identically
            // — the projection only ever bends the documented limit cases.
            foreach (string fileName in new[] { "race-arc.json", "caution-and-sectors.json" })
            {
                List<Step> steps = LoadTimeline(fileName, out _);
                var pipeline = new AdapterPipeline(new IRacingAdapter());
                int exact = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    Step step = steps[i];
                    bool reachable = step.Flag != TrackFlag.Red
                        && step.Caution == Caution.None
                        && step.Sectors.IsEmpty
                        && (step.Flag == TrackFlag.Yellow
                            ? step.Tier == Tier.Alert
                            : step.Tier == Tier.Ambient)
                        && step.Session != Session.PostRace;
                    if (!reachable)
                    {
                        continue;
                    }
                    SignalState got = pipeline.Map(SnapshotOf(step));
                    Assert.True(step.Flag == got.Flag && step.Tier == got.Tier
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
            public Expect(uint mask, TrackFlag flag, Tier tier, Caution caution,
                bool blackFlag = false, bool meatball = false, bool furled = false,
                StartPhase startPhase = StartPhase.Off, byte countdown = 0, string because = null)
            {
                Mask = mask;
                Flag = flag;
                Tier = tier;
                Caution = caution;
                BlackFlag = blackFlag;
                Meatball = meatball;
                Furled = furled;
                StartPhase = startPhase;
                Countdown = countdown;
                Because = because ?? string.Empty;
            }

            public uint Mask { get; }
            public TrackFlag Flag { get; }
            public Tier Tier { get; }
            public Caution Caution { get; }
            public bool BlackFlag { get; }
            public bool Meatball { get; }
            public bool Furled { get; }
            public StartPhase StartPhase { get; }
            public byte Countdown { get; }
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
                new Expect(IRacingAdapter.FlagGreenHeld, TrackFlag.None, Tier.Ambient, Caution.None,
                    startPhase: StartPhase.Set,
                    because: "greenHeld: green still furled — gantry Set, no flag yet"),
                new Expect(IRacingAdapter.FlagGreen, TrackFlag.Green, Tier.Alert, Caution.None),
                new Expect(0, TrackFlag.None, Tier.Ambient, Caution.None),
                new Expect(IRacingAdapter.FlagYellow, TrackFlag.Yellow, Tier.Ambient, Caution.None,
                    because: "displayed yellow: raw kills the generic Alert guess"),
                new Expect(IRacingAdapter.FlagYellow | IRacingAdapter.FlagYellowWaving,
                    TrackFlag.Yellow, Tier.Alert, Caution.None),
                new Expect(IRacingAdapter.FlagCaution | IRacingAdapter.FlagCautionWaving
                        | IRacingAdapter.FlagYellowWaving,
                    TrackFlag.Yellow, Tier.Urgent, Caution.SafetyCar,
                    because: "waving full-course caution: SC board over an urgent yellow field"),
                new Expect(IRacingAdapter.FlagOneLapToGreen | IRacingAdapter.FlagCaution,
                    TrackFlag.Yellow, Tier.Alert, Caution.SafetyCar,
                    startPhase: StartPhase.Ready,
                    because: "one-to-green: caution still up, gantry arms on the rolling restart"),
                new Expect(0, TrackFlag.None, Tier.Ambient, Caution.None),
                new Expect(IRacingAdapter.FlagFurled, TrackFlag.None, Tier.Ambient, Caution.None,
                    furled: true),
                new Expect(IRacingAdapter.FlagRepair, TrackFlag.None, Tier.Ambient, Caution.None,
                    meatball: true,
                    because: "repair: the meatball field (unified orange maps to the orthogonal dimension)"),
                new Expect(IRacingAdapter.FlagBlack, TrackFlag.None, Tier.Ambient, Caution.None,
                    blackFlag: true),
                new Expect(IRacingAdapter.FlagTenToGo, TrackFlag.None, Tier.Ambient, Caution.None,
                    countdown: 10),
                new Expect(IRacingAdapter.FlagRed, TrackFlag.Red, Tier.Urgent, Caution.None),
                new Expect(IRacingAdapter.FlagWhite, TrackFlag.White, Tier.Ambient, Caution.None),
                new Expect(IRacingAdapter.FlagCheckered, TrackFlag.Checkered, Tier.Ambient, Caution.None),
            };

            var pipeline = new AdapterPipeline(new IRacingAdapter());
            for (int i = 0; i < sequence.Length; i++)
            {
                Expect expect = sequence[i];
                SignalState got = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(expect.Mask));
                string context = $"step {i} (mask 0x{expect.Mask:X}) {expect.Because}";
                Assert.True(expect.Flag == got.Flag, $"{context}: flag {got.Flag}, want {expect.Flag}");
                Assert.True(expect.Tier == got.Tier, $"{context}: tier {got.Tier}, want {expect.Tier}");
                Assert.True(expect.Caution == got.Caution,
                    $"{context}: caution {got.Caution}, want {expect.Caution}");
                Assert.True(expect.BlackFlag == got.BlackFlag,
                    $"{context}: black {got.BlackFlag}, want {expect.BlackFlag}");
                Assert.True(expect.Meatball == got.Meatball,
                    $"{context}: meatball {got.Meatball}, want {expect.Meatball}");
                Assert.True(expect.Furled == got.Furled,
                    $"{context}: furled {got.Furled}, want {expect.Furled}");
                Assert.True(expect.StartPhase == got.StartPhase,
                    $"{context}: start phase {got.StartPhase}, want {expect.StartPhase}");
                Assert.True(expect.Countdown == got.CountdownLaps,
                    $"{context}: countdown {got.CountdownLaps}, want {expect.Countdown}");
                // Never fabricated from iRacing telemetry:
                Assert.True(got.BlackDetail == BlackDetail.None
                    || got.BlackDetail == BlackDetail.Disqualified,
                    $"{context}: DT/SG must never be guessed for iRacing");
            }
        }

        [Fact]
        public void RedDuringCautionIsATotalTakeover()
        {
            // Red + caution simultaneously: the adapter reports both; the
            // compositor resolves red on top and suppresses the board.
            var pipeline = new AdapterPipeline(new IRacingAdapter());
            SignalState state = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(
                IRacingAdapter.FlagRed | IRacingAdapter.FlagCaution));
            Assert.Equal(TrackFlag.Red, state.Flag);
            Assert.Equal(Caution.SafetyCar, state.Caution);

            Composition comp = Compositor.Select(state, connected: true);
            Assert.Equal(FieldKind.Red, comp.Field);
            Assert.Equal(BoardKind.None, comp.Board);
            Assert.False(comp.SectorStrip);
        }

        [Fact]
        public void PenaltyBitsComposeWithTheCautionRegime()
        {
            // Meatball + furled during a caution: the adapter carries all
            // three dimensions; the compositor stacks the yellow field, the
            // SC board and the furled frame.
            var pipeline = new AdapterPipeline(new IRacingAdapter());
            SignalState state = pipeline.Map(IRacingAdapterTests.IRacingSnapshot(
                IRacingAdapter.FlagCaution | IRacingAdapter.FlagRepair | IRacingAdapter.FlagFurled));
            Assert.Equal(Caution.SafetyCar, state.Caution);
            Assert.True(state.Meatball);
            Assert.True(state.Furled);

            Composition comp = Compositor.Select(state, connected: true);
            Assert.Equal(FieldKind.Yellow, comp.Field);
            Assert.Equal(BoardKind.SafetyCar, comp.Board);
            Assert.Equal(FrameKind.Furled, comp.Frame);
        }
    }
}
