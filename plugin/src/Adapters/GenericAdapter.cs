// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The generic game adapter: maps SimHub's unified Flag_* layer to a
// SignalState (docs/flag-grammar.md §10). The mapping contract is documented
// in docs/simhub-flag-properties.md ("Generic adapter mapping") — doc and
// code must state the same rules; change them together.

using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;
using Caution = Uniflag.Rendering.Grammar.Caution;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Catch-all adapter over the unified <c>DataCorePlugin.GameData.Flag_*</c>
    /// properties. Contract:
    /// <list type="bullet">
    /// <item><b>Track flag priority</b> when several are set:
    /// Yellow &gt; Blue &gt; White &gt; Checkered &gt; Green. The unified
    /// black and orange flags are NOT in this ladder — they map to the
    /// orthogonal <see cref="SignalState.BlackFlag"/> /
    /// <see cref="SignalState.Meatball"/> dimensions, so the compositor's
    /// demotion rule can keep them visible under a winning track flag.</item>
    /// <item><b>Tier heuristic</b>: yellow enters at
    /// <see cref="Tier.Alert"/> (the marshal-is-waving guess — the unified
    /// layer cannot distinguish displayed from waved); everything else at
    /// <see cref="Tier.Ambient"/>.</item>
    /// <item><b>Session</b>: see <see cref="MapSession"/>.</item>
    /// <item><b>Everything else</b> (caution, sectors, penalty details,
    /// start sequence, notices, advisories) is pinned to its default —
    /// those signals only exist in per-sim raw data and belong to the
    /// refiners.</item>
    /// </list>
    /// The unified layer never surfaces a red flag, so this adapter never
    /// emits <see cref="TrackFlag.Red"/> either.
    /// </summary>
    public sealed class GenericAdapter : IGameAdapter
    {
        /// <inheritdoc />
        public bool Matches(string gameName) => true;

        /// <inheritdoc />
        public void Map(TelemetrySnapshot snapshot, ref SignalState state)
        {
            TrackFlag flag = MapFlag(snapshot);
            state.Flag = flag;
            state.Tier = flag == TrackFlag.Yellow ? Tier.Alert : Tier.Ambient;
            state.BlackFlag = snapshot.FlagBlack;
            state.BlackDetail = BlackDetail.None;
            state.Meatball = snapshot.FlagOrange;
            state.Session = MapSession(snapshot.SessionTypeName, snapshot.GamePaused);
            state.Caution = Caution.None;
            state.Sectors = SectorSet.Empty;
            state.StartPhase = StartPhase.Off;
            state.StartLightsLit = 0;
            state.TimePenaltySeconds = 0;
            state.CountdownLaps = 0;
            state.Furled = false;
            state.IncidentWarning = false;
        }

        /// <summary>
        /// First-set-wins track-flag selection in the fixed priority order —
        /// yellow (danger) above all, then blue (traffic), then the
        /// informational flags. Black/orange are handled as orthogonal
        /// dimensions in <see cref="Map"/>, not here.
        /// </summary>
        public static TrackFlag MapFlag(TelemetrySnapshot snapshot)
        {
            if (snapshot.FlagYellow)
            {
                return TrackFlag.Yellow;
            }
            if (snapshot.FlagBlue)
            {
                return TrackFlag.Blue;
            }
            if (snapshot.FlagWhite)
            {
                return TrackFlag.White;
            }
            if (snapshot.FlagCheckered)
            {
                return TrackFlag.Checkered;
            }
            if (snapshot.FlagGreen)
            {
                return TrackFlag.Green;
            }
            return TrackFlag.None;
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
