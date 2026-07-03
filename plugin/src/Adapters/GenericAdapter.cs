// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The generic game adapter (docs/v2-plan.md M4 step 2): maps SimHub's
// unified Flag_* layer to a RenderState. The mapping contract is documented
// in docs/simhub-flag-properties.md ("Generic adapter mapping") — doc and
// code must state the same rules; change them together.

using Uniflag.Rendering;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Catch-all adapter over the unified <c>DataCorePlugin.GameData.Flag_*</c>
    /// properties. Contract:
    /// <list type="bullet">
    /// <item><b>Flag priority</b> when several are set:
    /// Yellow &gt; Blue &gt; Black &gt; White &gt; Checkered &gt; Green &gt;
    /// Orange — parity with the v1 NCalc formula (simhub/README.md).</item>
    /// <item><b>Wave heuristic</b>: Single when yellow wins, else None —
    /// the unified layer cannot distinguish displayed from waved flags.</item>
    /// <item><b>Session</b>: see <see cref="MapSession"/>.</item>
    /// <item><b>Caution / sectors</b>: always None / Empty. VSC, SC and
    /// sector-local yellows only exist in per-sim raw data (M10).</item>
    /// </list>
    /// The unified layer never surfaces a red flag, so this adapter never
    /// emits <see cref="Flag.Red"/> either.
    /// </summary>
    public sealed class GenericAdapter : IGameAdapter
    {
        /// <inheritdoc />
        public bool Matches(string gameName) => true;

        /// <inheritdoc />
        public void Map(TelemetrySnapshot snapshot, ref RenderState state)
        {
            Flag flag = MapFlag(snapshot);
            state.Flag = flag;
            state.Wave = flag == Flag.Yellow ? WaveLevel.Single : WaveLevel.None;
            state.Session = MapSession(snapshot.SessionTypeName, snapshot.GamePaused);
            state.Caution = Caution.None;
            state.Sectors = SectorSet.Empty;
        }

        /// <summary>
        /// First-set-wins flag selection in the fixed priority order —
        /// yellow (danger) above all, then the driver-directed flags, then
        /// the informational ones.
        /// </summary>
        public static Flag MapFlag(TelemetrySnapshot snapshot)
        {
            if (snapshot.FlagYellow)
            {
                return Flag.Yellow;
            }
            if (snapshot.FlagBlue)
            {
                return Flag.Blue;
            }
            if (snapshot.FlagBlack)
            {
                return Flag.Black;
            }
            if (snapshot.FlagWhite)
            {
                return Flag.White;
            }
            if (snapshot.FlagCheckered)
            {
                return Flag.Checkered;
            }
            if (snapshot.FlagGreen)
            {
                return Flag.Green;
            }
            if (snapshot.FlagOrange)
            {
                return Flag.Orange;
            }
            return Flag.None;
        }

        /// <summary>
        /// Session mapping from SimHub's <c>SessionTypeName</c>:
        /// <c>GamePaused</c> wins outright; null/empty → Unknown; names
        /// containing a pre-race keyword (practice, qualif*, test, warmup,
        /// hotlap, hotstint, superpole — covering iRacing's "Offline
        /// Testing"/"Lone Qualify", ACC's "HOTSTINT"/"SUPERPOLE", and the
        /// Codemasters "Practice n"/"Qualifying n" variants) → PreRace;
        /// then names containing "race" ("Race", "RACE", "Race 1") →
        /// Racing; anything else → Unknown. Pre-race keywords are checked
        /// before "race" so a hypothetical "Pre-Race Practice" cannot
        /// misroute. Matching is ordinal case-insensitive and
        /// allocation-free (no ToLower).
        /// </summary>
        public static Session MapSession(string sessionTypeName, bool gamePaused)
        {
            if (gamePaused)
            {
                return Session.Paused;
            }
            if (string.IsNullOrEmpty(sessionTypeName))
            {
                return Session.Unknown;
            }
            if (ContainsIgnoreCase(sessionTypeName, "practice")
                || ContainsIgnoreCase(sessionTypeName, "qualif")
                || ContainsIgnoreCase(sessionTypeName, "test")
                || ContainsIgnoreCase(sessionTypeName, "warmup")
                || ContainsIgnoreCase(sessionTypeName, "hotlap")
                || ContainsIgnoreCase(sessionTypeName, "hotstint")
                || ContainsIgnoreCase(sessionTypeName, "superpole"))
            {
                return Session.PreRace;
            }
            if (ContainsIgnoreCase(sessionTypeName, "race"))
            {
                return Session.Racing;
            }
            return Session.Unknown;
        }

        private static bool ContainsIgnoreCase(string haystack, string needle) =>
            haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
