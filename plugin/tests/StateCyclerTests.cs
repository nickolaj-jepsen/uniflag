// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// State-cycler tests: the debug tour is deterministic, covers the whole
// Grammar signal vocabulary, applies entries in order on the override
// channel with connected=true, and stopping clears the override (falling
// the renderer back to its normal input) instead of clobbering it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Rendering.Grammar;
using Xunit;

namespace Uniflag.Tests
{
    public class StateCyclerTests
    {
        private static void AssertStatesEqual(SignalState want, SignalState got)
        {
            Assert.Equal(want.Flag, got.Flag);
            Assert.Equal(want.Tier, got.Tier);
            Assert.Equal(want.BlackFlag, got.BlackFlag);
            Assert.Equal(want.BlackDetail, got.BlackDetail);
            Assert.Equal(want.Meatball, got.Meatball);
            Assert.Equal(want.Session, got.Session);
            Assert.Equal(want.Caution, got.Caution);
            Assert.Equal(want.Sectors, got.Sectors);
            Assert.Equal(want.StartPhase, got.StartPhase);
            Assert.Equal(want.StartLightsLit, got.StartLightsLit);
            Assert.Equal(want.TimePenaltySeconds, got.TimePenaltySeconds);
            Assert.Equal(want.CountdownLaps, got.CountdownLaps);
            Assert.Equal(want.Furled, got.Furled);
            Assert.Equal(want.IncidentWarning, got.IncidentWarning);
        }

        [Fact]
        public void SequenceIsDeterministic()
        {
            IReadOnlyList<SignalState> a = StateCycler.BuildSequence();
            IReadOnlyList<SignalState> b = StateCycler.BuildSequence();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                AssertStatesEqual(a[i], b[i]);
            }
        }

        [Fact]
        public void SequenceCoversTheFullSignalVocabulary()
        {
            IReadOnlyList<SignalState> sequence = StateCycler.BuildSequence();
            var flags = new HashSet<TrackFlag>();
            var tiers = new HashSet<Tier>();
            var cautions = new HashSet<Caution>();
            var details = new HashSet<BlackDetail>();
            var phases = new HashSet<StartPhase>();
            int sectorStates = 0;
            bool meatball = false, furled = false, incident = false;
            bool timePenalty = false, countdown = false, blackFlag = false;
            foreach (SignalState state in sequence)
            {
                flags.Add(state.Flag);
                tiers.Add(state.Tier);
                cautions.Add(state.Caution);
                details.Add(state.BlackDetail);
                phases.Add(state.StartPhase);
                if (!state.Sectors.IsEmpty)
                {
                    sectorStates++;
                }
                meatball |= state.Meatball;
                furled |= state.Furled;
                incident |= state.IncidentWarning;
                timePenalty |= state.TimePenaltySeconds > 0;
                countdown |= state.CountdownLaps > 0;
                blackFlag |= state.BlackFlag;
            }

            foreach (TrackFlag flag in (TrackFlag[])Enum.GetValues(typeof(TrackFlag)))
            {
                Assert.Contains(flag, flags);
            }
            foreach (Tier tier in (Tier[])Enum.GetValues(typeof(Tier)))
            {
                Assert.Contains(tier, tiers);
            }
            foreach (Caution caution in (Caution[])Enum.GetValues(typeof(Caution)))
            {
                Assert.Contains(caution, cautions);
            }
            foreach (BlackDetail detail in (BlackDetail[])Enum.GetValues(typeof(BlackDetail)))
            {
                Assert.Contains(detail, details);
            }
            foreach (StartPhase phase in (StartPhase[])Enum.GetValues(typeof(StartPhase)))
            {
                Assert.Contains(phase, phases);
            }
            Assert.True(sectorStates >= 3, "tour must show several sector-strip combinations");
            Assert.True(meatball && furled && incident && timePenalty && countdown && blackFlag,
                "tour must show every orthogonal dimension at least once");
        }

        [Fact]
        public void AdvanceAppliesTheSequenceInOrderConnectedAndWraps()
        {
            var applied = new List<KeyValuePair<SignalState, bool>>();
            int cleared = 0;
            var cycler = new StateCycler(
                (state, connected) => applied.Add(new KeyValuePair<SignalState, bool>(state, connected)),
                () => cleared++);
            IReadOnlyList<SignalState> sequence = StateCycler.BuildSequence();

            int steps = sequence.Count + 3; // wrap past the end of the tour
            for (int i = 0; i < steps; i++)
            {
                cycler.Advance();
            }

            Assert.Equal(steps, applied.Count);
            Assert.Equal(0, cleared); // Advance never touches the clear path
            for (int i = 0; i < steps; i++)
            {
                Assert.True(applied[i].Value, $"step {i} must apply connected=true");
                AssertStatesEqual(sequence[i % sequence.Count], applied[i].Key);
            }
        }

        [Fact]
        public void StopClearsTheOverrideAfterTheLastStep()
        {
            var gate = new object();
            // Interleaved event log: "step" per override application,
            // "clear" per clear-override call — Stop's clear must come after
            // every step, exactly once.
            var events = new List<string>();
            using var cycler = new StateCycler(
                (state, connected) =>
                {
                    Assert.True(connected, "tour steps always apply connected=true");
                    lock (gate)
                    {
                        events.Add("step");
                    }
                },
                () =>
                {
                    lock (gate)
                    {
                        events.Add("clear");
                    }
                });

            cycler.Start();
            Assert.True(cycler.IsRunning);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (gate)
                {
                    if (events.Count >= 1)
                    {
                        break;
                    }
                }
                Assert.True(sw.ElapsedMilliseconds < 10000, "timed out waiting for the first cycler step");
                Thread.Sleep(10);
            }
            cycler.Stop();
            Assert.False(cycler.IsRunning);

            lock (gate)
            {
                // Stop drains the in-flight step, then clears the override —
                // always the final event, and never a state application.
                Assert.Equal("clear", events[events.Count - 1]);
                Assert.Single(events.FindAll(e => e == "clear"));
            }
        }

        [Fact]
        public void StopWithoutStartDoesNotClear()
        {
            int cleared = 0;
            var cycler = new StateCycler((state, connected) => { }, () => cleared++);
            // Never started: there is no override to clear — a spurious
            // clear could cancel someone else's override.
            cycler.Stop();
            Assert.Equal(0, cleared);
        }
    }
}
