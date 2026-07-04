// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The C# DUMPER for the C#-authored golden corpus — the named regen tool
// wired into the [windows] leg of `just golden-regen`. OPT-IN like the
// hardware test: gated on the UNIFLAG_REGEN_PLUGIN_GOLDENS environment
// variable so a normal test run
// never mutates fixtures (regeneration only ever happens deliberately, in
// reviewed commits — the two-corpus policy). It never touches
// testdata/frames/ (the ported-parity set): only testdata/frames-plugin/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// <see cref="FactAttribute"/> that skips unless
    /// <see cref="RegenVariable"/> is set — the same derived-attribute
    /// gating pattern as <see cref="DeviceFactAttribute"/>.
    /// </summary>
    public sealed class PluginGoldenRegenFactAttribute : FactAttribute
    {
        /// <summary>Environment variable that arms the regeneration run.</summary>
        public const string RegenVariable = "UNIFLAG_REGEN_PLUGIN_GOLDENS";

        public PluginGoldenRegenFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RegenVariable)))
            {
                Skip = "set " + RegenVariable + "=1 to regenerate testdata/frames-plugin/ "
                    + "(deliberate, reviewed commits only — run via `just golden-regen`)";
            }
        }
    }

    public class PluginGoldenDumper
    {
        /// <summary>
        /// Deterministic regeneration of testdata/frames-plugin/: renders
        /// every ledger entry in table order, deletes stale .rgb files that
        /// left the table, and rewrites manifest.json (LF endings, UTF-8,
        /// no BOM — testdata/** is `-text` in .gitattributes so git never
        /// rewrites it). Paint is a pure function of
        /// (state, frame, flag_age, connected), so a second run produces
        /// zero byte changes.
        /// </summary>
        [PluginGoldenRegenFact]
        public void RegenerateCSharpAuthoredCorpus()
        {
            string dir = PluginGoldenScenarios.CorpusDir;
            Directory.CreateDirectory(dir);

            // Stale-file cleanup: anything not in the ledger goes. The
            // manifest is rewritten wholesale below.
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginGoldenScenarios.Scenario scenario in PluginGoldenScenarios.Table)
            {
                wanted.Add(scenario.File);
            }
            foreach (string path in Directory.GetFiles(dir, "*.rgb"))
            {
                if (!wanted.Contains(Path.GetFileName(path)))
                {
                    File.Delete(path);
                }
            }

            // Scenario-table order, byte-for-byte reproducible.
            foreach (PluginGoldenScenarios.Scenario scenario in PluginGoldenScenarios.Table)
            {
                byte[] frame = PluginGoldenScenarios.Render(scenario);
                File.WriteAllBytes(Path.Combine(dir, scenario.File), frame);
            }

            File.WriteAllText(
                Path.Combine(dir, "manifest.json"),
                PluginGoldenScenarios.BuildManifestJson(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
