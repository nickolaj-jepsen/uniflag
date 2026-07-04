// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Conformance tests for the Grammar golden corpus: every ledger scenario is
// replayed and compared byte-for-byte against testdata/frames-grammar/, and
// the on-disk manifest must match the in-code ledger exactly so any table
// change forces a deliberate `just golden-regen` commit.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Uniflag.Tests
{
    public class GrammarGoldenFrameTests
    {
        public static IEnumerable<object[]> ScenarioNames =>
            GrammarGoldenScenarios.Table.Select(sc => new object[] { sc.Name });

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void FrameMatchesGolden(string name)
        {
            var sc = GrammarGoldenScenarios.Table.Single(entry => entry.Name == name);
            string path = Path.Combine(GrammarGoldenDumper.CorpusDir, sc.File);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"missing Grammar golden {path}; regenerate via `just golden-regen`", path);
            }
            Assert.Equal(File.ReadAllBytes(path), GrammarGoldenScenarios.Render(sc));
        }

        [Fact]
        public void ManifestMatchesTheScenarioTable()
        {
            string path = Path.Combine(GrammarGoldenDumper.CorpusDir, "manifest.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"missing Grammar manifest {path}; regenerate via `just golden-regen`", path);
            }
            string onDisk = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(GrammarGoldenScenarios.BuildManifestJson(), onDisk);
        }

        [Fact]
        public void ScenarioNamesAreUnique()
        {
            var names = GrammarGoldenScenarios.Table.Select(sc => sc.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }
}
