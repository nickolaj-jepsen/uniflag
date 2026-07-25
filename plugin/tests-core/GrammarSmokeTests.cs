// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Whole-scenario smoke pass over the Grammar catalogue. Deliberately pins no
// pixels — while the visual design is still moving, byte-exactness is the
// wrong contract for the painters (docs/flag-grammar.md §11).
//
// What it asserts are the invariants that hold no matter how the painters are
// tuned: every scenario replays end to end without throwing, produces a whole
// frame, renders deterministically, and is dark only where darkness is the
// signal. That catches the failure the unit tests miss — a compositor/envelope
// interaction that only appears under full replay — and never needs
// regenerating.
//
// Visual review is the frame viewer's job, not a test's.

using System;
using System.Collections.Generic;
using System.Linq;
using Uniflag.Tools;
using Xunit;

namespace Uniflag.Tests
{
    public class GrammarSmokeTests
    {
        /// <summary>
        /// Scenarios whose sampled frame is legitimately all-black: the signal
        /// IS the darkness. Only the urgent strobe's off-phase qualifies —
        /// `gantry_go_lights_out` still paints its board chrome and the dark
        /// gantry sockets, which is the point (lights out, panel not dead).
        /// Membership is asserted exactly in both directions, so a scenario
        /// that silently goes dark fails as loudly as one that silently lights up.
        /// </summary>
        private static readonly HashSet<string> ExpectedBlank = new HashSet<string>
        {
            "yellow_t2_strobe_off",
        };

        public static IEnumerable<object[]> ScenarioNames =>
            ScenarioCatalogue.Table.Select(sc => new object[] { sc.Name });

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void ScenarioRendersAWholeFrame(string name)
        {
            byte[] frame = ScenarioCatalogue.Render(ScenarioCatalogue.Find(name));
            Assert.Equal(Rendering.FrameBuffer.ByteLength, frame.Length);
        }

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void ScenarioRendersDeterministically(string name)
        {
            var sc = ScenarioCatalogue.Find(name);
            Assert.Equal(ScenarioCatalogue.Render(sc), ScenarioCatalogue.Render(sc));
        }

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void ScenarioIsDarkOnlyWhereDarknessIsTheSignal(string name)
        {
            byte[] frame = ScenarioCatalogue.Render(ScenarioCatalogue.Find(name));
            bool blank = frame.All(b => b == 0);
            Assert.Equal(ExpectedBlank.Contains(name), blank);
        }

        /// <summary>
        /// Walk a window past the sample frame: sample points are chosen to sit
        /// on interesting animation phases, and the frames either side exercise
        /// the transitions into and out of them.
        /// </summary>
        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void ScenarioSurvivesItsSampleWindow(string name)
        {
            var sc = ScenarioCatalogue.Find(name);
            for (uint f = sc.SampleFrame; f <= sc.SampleFrame + 4; f++)
            {
                byte[] frame = ScenarioCatalogue.Render(sc, f);
                Assert.Equal(Rendering.FrameBuffer.ByteLength, frame.Length);
            }
        }

        [Fact]
        public void CatalogueIsWellFormed()
        {
            var names = ScenarioCatalogue.Table.Select(sc => sc.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());

            foreach (var sc in ScenarioCatalogue.Table)
            {
                Assert.NotEmpty(sc.Steps);
                Assert.Equal(0u, sc.Steps[0].Frame);
                for (int i = 1; i < sc.Steps.Length; i++)
                {
                    Assert.True(
                        sc.Steps[i].Frame > sc.Steps[i - 1].Frame,
                        $"{sc.Name}: script steps must be in ascending frame order");
                }
                Assert.True(
                    sc.SampleFrame >= sc.Steps[sc.Steps.Length - 1].Frame,
                    $"{sc.Name}: sample frame precedes the last scripted state change");
                Assert.False(string.IsNullOrWhiteSpace(sc.Description), $"{sc.Name}: missing description");
            }
        }

        [Fact]
        public void FindReturnsNullForAnUnknownScenario()
        {
            Assert.Null(ScenarioCatalogue.Find("no_such_scenario"));
        }
    }
}
