// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Regeneration tool for the Grammar golden corpus (testdata/frames-grammar/).
// Mirrors PluginGoldenDumper: opt-in via UNIFLAG_REGEN_GRAMMAR_GOLDENS so a
// plain test run can never rewrite fixtures; run via the [windows]
// `just golden-regen` leg, in deliberate reviewed commits only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Uniflag.Tests
{
    public sealed class GrammarGoldenRegenFactAttribute : FactAttribute
    {
        public const string RegenVariable = "UNIFLAG_REGEN_GRAMMAR_GOLDENS";

        public GrammarGoldenRegenFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(RegenVariable) == null)
            {
                Skip = $"regeneration tool — set {RegenVariable}=1 (via `just golden-regen`) to run";
            }
        }
    }

    public class GrammarGoldenDumper
    {
        internal static string CorpusDir => Path.Combine(RepoPaths.RepoRoot, "testdata", "frames-grammar");

        [GrammarGoldenRegenFact]
        public void RegenerateGrammarCorpus()
        {
            Directory.CreateDirectory(CorpusDir);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sc in GrammarGoldenScenarios.Table)
            {
                keep.Add(sc.File);
                File.WriteAllBytes(Path.Combine(CorpusDir, sc.File), GrammarGoldenScenarios.Render(sc));
            }

            foreach (string stale in Directory.GetFiles(CorpusDir, "*.rgb"))
            {
                if (!keep.Contains(Path.GetFileName(stale)))
                {
                    File.Delete(stale);
                }
            }

            File.WriteAllText(
                Path.Combine(CorpusDir, "manifest.json"),
                GrammarGoldenScenarios.BuildManifestJson(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
