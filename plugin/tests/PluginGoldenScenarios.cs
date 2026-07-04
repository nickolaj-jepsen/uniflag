// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The scenario ledger for the C#-AUTHORED golden corpus at
// testdata/frames-plugin/. This corpus is deliberately separate from the
// ported-parity set at testdata/frames/: that one was produced by the Rust
// dumper and is permanently frozen (no regen path); this one pins the
// penalty effects, is produced by the C# dumper in PluginGoldenDumper.cs
// (the [windows] leg of `just golden-regen`), and stays regenerable — its
// baselines are pending maintainer visual review in the WPF preview, and
// regeneration keeps revision cheap. Names are unique lowercase_snake and
// the table is append-only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Uniflag.Rendering;

namespace Uniflag.Tests
{
    /// <summary>
    /// Single source of truth for the C#-authored corpus: the scenario
    /// table, the manifest serialization the dumper writes, and the
    /// renderer both the dumper and the golden theory call. Frame choices
    /// pin each effect's onset, steady state and both phases of every
    /// blink/breathe (rationale on each entry).
    /// </summary>
    internal static class PluginGoldenScenarios
    {
        /// <summary>Every baseline in this corpus awaits this review gate.</summary>
        internal const string ReviewMarker = "pending maintainer visual review (WPF preview)";

        /// <summary>One ledger entry — everything Paint needs plus the fixture file.</summary>
        internal sealed class Scenario
        {
            public Scenario(string name, RenderState state, uint frame, string description)
            {
                Name = name;
                File = name + ".rgb";
                State = state;
                Frame = frame;
                Description = description + "; " + ReviewMarker;
            }

            public string Name { get; }
            public string File { get; }
            public RenderState State { get; }
            public uint Frame { get; }

            /// <summary>Flag age — no scenario here exercises onset animations.</summary>
            public uint FlagAge => 100;

            public bool Connected => true;

            public string Description { get; }
        }

        internal static string CorpusDir =>
            Path.Combine(RepoPaths.RepoRoot, "testdata", "frames-plugin");

        internal static IReadOnlyList<Scenario> Table { get; } = Build();

        private static IReadOnlyList<Scenario> Build()
        {
            var table = new List<Scenario>
            {
                // --- Slowdown board: severity digit static at 1, blinking
                //     2 Hz at 2 (period 30, on 18) and 4 Hz at 3 (period 15,
                //     on 9) — both phases pinned per blinking severity.
                new Scenario(
                    "slowdown_sev1_static",
                    Penalty(slowdown: 1),
                    frame: 0,
                    "slowdown severity 1: white SLOW + static orange digit 1"),
                new Scenario(
                    "slowdown_sev2_blink_on",
                    Penalty(slowdown: 2),
                    frame: 10,
                    "slowdown severity 2, digit 2 in the 2 Hz on-phase (10 < 18)"),
                new Scenario(
                    "slowdown_sev2_blink_off",
                    Penalty(slowdown: 2),
                    frame: 20,
                    "slowdown severity 2, digit blanked in the 2 Hz off-phase (20 >= 18)"),
                new Scenario(
                    "slowdown_sev3_blink_on",
                    Penalty(slowdown: 3),
                    frame: 5,
                    "slowdown severity 3, digit 3 in the 4 Hz on-phase (5 < 9)"),
                new Scenario(
                    "slowdown_sev3_blink_off",
                    Penalty(slowdown: 3),
                    frame: 12,
                    "slowdown severity 3, digit blanked in the 4 Hz off-phase (12 >= 9)"),

                // --- Meatball board: 1 Hz breathe (period 60), multiplier
                //     m = 180 + breathe*75/255 — start (m 217), peak (255,
                //     frame 15) and trough (180, frame 45) pinned.
                new Scenario(
                    "meatball_breathe_start",
                    Penalty(meatball: true),
                    frame: 0,
                    "meatball disc at the breathe start (envelope 128, m 217)"),
                new Scenario(
                    "meatball_breathe_peak",
                    Penalty(meatball: true),
                    frame: 15,
                    "meatball disc at the breathe peak (envelope 255, m 255 — full orange)"),
                new Scenario(
                    "meatball_breathe_trough",
                    Penalty(meatball: true),
                    frame: 45,
                    "meatball disc at the breathe trough (envelope 1, m 180)"),
                new Scenario(
                    "meatball_with_sector_band",
                    Penalty(meatball: true, sectorBits: 0b010),
                    frame: 0,
                    "sector band S2 overlays the meatball board (bottom rows, on-phase)"),

                // --- Black-flag DT/SG markers over the golden-frozen X.
                //     Frame 25 = X breathe peak (grey 130); frame 75 = X
                //     trough (grey 0) leaving the white marker alone.
                new Scenario(
                    "black_drive_through",
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.DriveThrough),
                    frame: 25,
                    "black flag X at breathe peak with the white DT marker"),
                new Scenario(
                    "black_stop_and_go",
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.StopAndGo),
                    frame: 25,
                    "black flag X at breathe peak with the white SG marker"),
                new Scenario(
                    "black_drive_through_x_trough",
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.DriveThrough),
                    frame: 75,
                    "X envelope at 0 (frame 75): only the DT marker is visible"),

                // --- Furled warning accent: 2 Hz blink (period 30, on 18),
                //     over a dark base, a bright base, and a caution board.
                new Scenario(
                    "furled_over_race_idle_on",
                    Penalty(furled: true),
                    frame: 10,
                    "furled tile blinked on over the race-idle marker"),
                new Scenario(
                    "furled_over_race_idle_off",
                    Penalty(furled: true),
                    frame: 20,
                    "furled off-phase: base race-idle marker only (accent leaves no residue)"),
                new Scenario(
                    "furled_over_yellow_static",
                    Penalty(flag: Flag.Yellow, furled: true),
                    frame: 0,
                    "furled tile over the static-yellow cloth wave (bright base legibility)"),
                new Scenario(
                    "furled_over_safety_car_board",
                    Penalty(caution: Caution.SafetyCar, furled: true),
                    frame: 0,
                    "furled tile over the SC board (accent wins the top border pixels)"),

                // --- Precedence pins: byte-level proof of the documented
                //     ladder disconnected > red > VSC/SC > slowdown >
                //     meatball > per-flag, and red suppressing overlays.
                new Scenario(
                    "precedence_red_suppresses_penalties",
                    Penalty(
                        flag: Flag.Red,
                        slowdown: 3,
                        meatball: true,
                        furled: true,
                        sectorBits: 0b111),
                    frame: 100,
                    "red beats every penalty and suppresses both overlays (pure post-onset red)"),
                new Scenario(
                    "precedence_caution_beats_slowdown",
                    Penalty(caution: Caution.SafetyCar, slowdown: 3),
                    frame: 0,
                    "SC board over a pending severity-3 slowdown (caution outranks penalties)"),
                new Scenario(
                    "precedence_slowdown_beats_meatball",
                    Penalty(slowdown: 2, meatball: true),
                    frame: 10,
                    "slowdown board wins over a simultaneous meatball"),
                new Scenario(
                    "precedence_meatball_beats_black",
                    Penalty(flag: Flag.Black, blackDetail: BlackFlagDetail.DriveThrough, meatball: true),
                    frame: 25,
                    "meatball board wins over the black-flag base (DT detail deferred)"),
            };

            var seen = new HashSet<string>();
            foreach (Scenario scenario in table)
            {
                if (!seen.Add(scenario.Name))
                {
                    throw new InvalidOperationException(
                        $"duplicate scenario name '{scenario.Name}' in the plugin golden table");
                }
            }
            return table;
        }

        /// <summary>Race-session state with the given penalty/flag dimensions set.</summary>
        private static RenderState Penalty(
            Flag flag = Flag.None,
            Caution caution = Caution.None,
            byte sectorBits = 0,
            byte slowdown = 0,
            bool meatball = false,
            BlackFlagDetail blackDetail = BlackFlagDetail.None,
            bool furled = false)
        {
            RenderState state = RenderState.Default;
            state.Flag = flag;
            state.Session = Session.Racing;
            state.Caution = caution;
            state.Sectors = SectorSet.FromBits(sectorBits);
            state.Slowdown = slowdown;
            state.Meatball = meatball;
            state.BlackDetail = blackDetail;
            state.Furled = furled;
            return state;
        }

        /// <summary>Render one scenario into a fresh 3072-byte frame.</summary>
        internal static byte[] Render(Scenario scenario)
        {
            var frameBuffer = new FrameBuffer();
            Effects.Paint(frameBuffer, scenario.State, scenario.Frame, scenario.FlagAge, scenario.Connected);
            return frameBuffer.Pixels;
        }

        /// <summary>
        /// The manifest exactly as the dumper writes it: the frames-corpus
        /// format (name/file/state/frame/flag_age/connected in table order,
        /// PascalCase enum names, 2-space indent, LF endings) extended with
        /// the penalty state fields and a per-entry description carrying
        /// the review marker. Deterministic: invariant culture, no
        /// wall-clock input.
        /// </summary>
        internal static string BuildManifestJson()
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < Table.Count; i++)
            {
                Scenario s = Table[i];
                sb.Append("  {\n");
                sb.Append("    \"name\": \"").Append(s.Name).Append("\",\n");
                sb.Append("    \"file\": \"").Append(s.File).Append("\",\n");
                sb.Append("    \"state\": {\n");
                sb.Append("      \"flag\": \"").Append(s.State.Flag).Append("\",\n");
                sb.Append("      \"wave\": \"").Append(s.State.Wave).Append("\",\n");
                sb.Append("      \"session\": \"").Append(s.State.Session).Append("\",\n");
                sb.Append("      \"caution\": \"").Append(s.State.Caution).Append("\",\n");
                sb.Append("      \"sectors\": [").Append(SectorList(s.State.Sectors)).Append("],\n");
                sb.Append("      \"slowdown\": ")
                    .Append(s.State.Slowdown.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"meatball\": ").Append(s.State.Meatball ? "true" : "false").Append(",\n");
                sb.Append("      \"black_detail\": \"").Append(s.State.BlackDetail).Append("\",\n");
                sb.Append("      \"furled\": ").Append(s.State.Furled ? "true" : "false").Append('\n');
                sb.Append("    },\n");
                sb.Append("    \"frame\": ").Append(s.Frame.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("    \"flag_age\": ").Append(s.FlagAge.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("    \"connected\": ").Append(s.Connected ? "true" : "false").Append(",\n");
                sb.Append("    \"description\": \"").Append(s.Description).Append("\"\n");
                sb.Append(i + 1 < Table.Count ? "  },\n" : "  }\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static string SectorList(SectorSet sectors)
        {
            var parts = new List<string>();
            for (int sector = 1; sector <= 3; sector++)
            {
                if (sectors.Contains(sector))
                {
                    parts.Add(sector.ToString(CultureInfo.InvariantCulture));
                }
            }
            return string.Join(", ", parts);
        }
    }
}
