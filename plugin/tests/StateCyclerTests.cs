// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// State-cycler tests: the debug tour is deterministic, covers the whole
// flag/wave/caution/sector vocabulary, applies entries in order on the
// override channel with connected=true, and stopping clears the override
// (falling the renderer back to its normal input) instead of clobbering it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class StateCyclerTests
    {
        private static void AssertStatesEqual(RenderState want, RenderState got)
        {
            Assert.Equal(want.Flag, got.Flag);
            Assert.Equal(want.Wave, got.Wave);
            Assert.Equal(want.Session, got.Session);
            Assert.Equal(want.Caution, got.Caution);
            Assert.Equal(want.Sectors, got.Sectors);
        }

        private static byte MaskBits(SectorSet sectors)
        {
            byte bits = 0;
            for (int sector = 1; sector <= 3; sector++)
            {
                if (sectors.Contains(sector))
                {
                    bits |= (byte)(1 << (sector - 1));
                }
            }
            return bits;
        }

        [Fact]
        public void SequenceIsDeterministic()
        {
            IReadOnlyList<RenderState> a = StateCycler.BuildSequence();
            IReadOnlyList<RenderState> b = StateCycler.BuildSequence();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                AssertStatesEqual(a[i], b[i]);
            }
        }

        [Fact]
        public void SequenceCoversTheFullStateVocabulary()
        {
            IReadOnlyList<RenderState> sequence = StateCycler.BuildSequence();
            var flags = new HashSet<Flag>();
            var waves = new HashSet<WaveLevel>();
            var cautions = new HashSet<Caution>();
            var masks = new HashSet<byte>();
            foreach (RenderState state in sequence)
            {
                flags.Add(state.Flag);
                waves.Add(state.Wave);
                cautions.Add(state.Caution);
                masks.Add(MaskBits(state.Sectors));
            }

            foreach (Flag flag in (Flag[])Enum.GetValues(typeof(Flag)))
            {
                Assert.Contains(flag, flags);
            }
            foreach (WaveLevel wave in (WaveLevel[])Enum.GetValues(typeof(WaveLevel)))
            {
                Assert.Contains(wave, waves);
            }
            foreach (Caution caution in (Caution[])Enum.GetValues(typeof(Caution)))
            {
                Assert.Contains(caution, cautions);
            }
            for (byte bits = 0; bits <= 7; bits++)
            {
                Assert.Contains(bits, masks);
            }
        }

        [Fact]
        public void AdvanceAppliesTheSequenceInOrderConnectedAndWraps()
        {
            var applied = new List<KeyValuePair<RenderState, bool>>();
            int cleared = 0;
            var cycler = new StateCycler(
                (state, connected) => applied.Add(new KeyValuePair<RenderState, bool>(state, connected)),
                () => cleared++);
            IReadOnlyList<RenderState> sequence = StateCycler.BuildSequence();

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
