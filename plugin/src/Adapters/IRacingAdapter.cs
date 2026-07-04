// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The iRacing raw-telemetry refiner: layered after GenericAdapter, it
// overrides or enriches ONLY where the raw SessionFlags bitmask is better
// than the unified Flag_* layer. Every bit value below was verified against
// the iRacingSDK.dll shipped inside SimHub 9.11.21 (the assembly SimHub's
// own reader consumes), and SimHub's unified mapping was IL-verified for
// cross-checking — both documented in docs/simhub-flag-properties.md
// ("iRacing"), which holds the mapping contract; change doc and code together.

using System;
using Uniflag.Rendering;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Refines the generic mapping with iRacing raw data. What raw is
    /// better at (and what this adapter therefore touches):
    /// <list type="bullet">
    /// <item><b>Red flag</b> — the unified layer never surfaces red;
    /// SessionFlags bit 0x10 does.</item>
    /// <item><b>Wave level</b> — unified <c>Flag_Yellow</c> cannot tell a
    /// displayed yellow from a waved one (the generic adapter guesses
    /// Single); <c>yellowWaving</c>/<c>cautionWaving</c> decide it.</item>
    /// <item><b>Caution board</b> — <c>caution</c>/<c>cautionWaving</c>
    /// (0x4000/0x8000) map to the SC board: an iRacing full-course caution
    /// is a deployed pace car, and iRacing has no VSC concept.</item>
    /// <item><b>Penalties</b> — <c>repair</c> (0x100000) → meatball,
    /// <c>furled</c> (0x80000) → furled warning accent,
    /// <c>disqualify</c> (0x20000) → black flag when nothing else claims
    /// the base. iRacing exposes <b>no</b> graded slow-down meter and no
    /// DT-vs-SG distinction in telemetry (verified; third-party plugins
    /// derive their "slow down" alerts from the same furled bit), so
    /// <see cref="RenderState.Slowdown"/> and
    /// <see cref="RenderState.BlackDetail"/> stay at their defaults here.</item>
    /// </list>
    /// Checkered / white / green / black are single raw bits that the
    /// unified layer already mirrors 1:1 (IL-verified). Blue is <b>not</b> a
    /// pure mirror: SimHub derives <c>Flag_Blue = blue &amp;&amp; !green</c>
    /// (a set green bit suppresses unified blue — its deliberate fix for the
    /// spurious start-window blues of SimHub issue #436). This adapter
    /// deliberately does <b>not</b> restore blue from raw during that
    /// overlap: green is the flag that matters at a start/restart, and
    /// honouring the suppression keeps the panel consistent with every other
    /// SimHub-driven display. The one restore is <c>greenHeld</c> (0x400),
    /// which the unified layer drops and this adapter surfaces when no other
    /// flag won.
    /// Pure and allocation-free per call (runs at ~60 Hz).
    /// </summary>
    public sealed class IRacingAdapter : IGameAdapter
    {
        /// <summary>
        /// SimHub's <c>GameData.GameName</c> for iRacing — also names the
        /// <c>PluginsData\IRacing</c> per-game settings folder.
        /// </summary>
        public const string IRacingGameName = "IRacing";

        // irsdk_Flags bit values, verified against iRacingSDK.SessionFlags
        // in the SimHub-shipped iRacingSDK.dll (9.11.21). Do not trust
        // prose tables — this project has been burned before (mGamePhase).
        internal const uint FlagCheckered = 0x00000001;
        internal const uint FlagWhite = 0x00000002;
        internal const uint FlagGreen = 0x00000004;
        internal const uint FlagYellow = 0x00000008;
        internal const uint FlagRed = 0x00000010;
        internal const uint FlagBlue = 0x00000020;
        internal const uint FlagDebris = 0x00000040;
        internal const uint FlagCrossed = 0x00000080;
        internal const uint FlagYellowWaving = 0x00000100;
        internal const uint FlagOneLapToGreen = 0x00000200;
        internal const uint FlagGreenHeld = 0x00000400;
        internal const uint FlagTenToGo = 0x00000800;
        internal const uint FlagFiveToGo = 0x00001000;
        internal const uint FlagRandomWaving = 0x00002000;
        internal const uint FlagCaution = 0x00004000;
        internal const uint FlagCautionWaving = 0x00008000;
        internal const uint FlagBlack = 0x00010000;
        internal const uint FlagDisqualify = 0x00020000;
        internal const uint FlagServicible = 0x00040000;
        internal const uint FlagFurled = 0x00080000;
        internal const uint FlagRepair = 0x00100000;

        /// <inheritdoc />
        public bool Matches(string gameName) =>
            string.Equals(gameName, IRacingGameName, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public void Map(TelemetrySnapshot snapshot, ref RenderState state)
        {
            if (!snapshot.HasRawSessionFlags)
            {
                // Raw layer unavailable this tick (shape drift, no sample
                // yet): the generic unified result stands untouched.
                return;
            }
            uint bits = snapshot.RawSessionFlags;

            // Red — raw-only. iRacing has no red wave levels; clear the
            // wave so a simultaneous yellowWaving can't leak into red.
            if ((bits & FlagRed) != 0)
            {
                state.Flag = Flag.Red;
                state.Wave = WaveLevel.None;
            }

            // Full-course caution → SC board (pace car; no VSC in iRacing).
            if ((bits & (FlagCaution | FlagCautionWaving)) != 0)
            {
                state.Caution = Caution.SafetyCar;
            }

            // Wave refinement: only for a yellow base (never fabricate a
            // flag here — SimHub's unified Flag_Yellow already ORs the
            // yellow AND caution bits, so the generic adapter owns the
            // flag choice). Waving bits → Single; displayed-only → None.
            // iRacing has no double-waved concept, so Double never appears.
            if (state.Flag == Flag.Yellow)
            {
                state.Wave = (bits & (FlagYellowWaving | FlagCautionWaving)) != 0
                    ? WaveLevel.Single
                    : WaveLevel.None;
            }

            // Conservative enrichment when the unified layer mapped nothing:
            // disqualify shows the black flag (the sim's own presentation),
            // greenHeld is a green the unified layer drops. Never outranks
            // a flag another bit already won — the generic priority order
            // is the contract.
            if (state.Flag == Flag.None)
            {
                if ((bits & FlagDisqualify) != 0)
                {
                    state.Flag = Flag.Black;
                }
                else if ((bits & FlagGreenHeld) != 0)
                {
                    state.Flag = Flag.Green;
                }
            }

            // Penalty dimensions (host-only; see RenderState). repair also
            // sets unified Flag_Orange → the generic result keeps
            // Flag.Orange, but the meatball board outranks the orange base
            // in the precedence ladder, so the panel shows the meatball.
            state.Meatball = (bits & FlagRepair) != 0;
            state.Furled = (bits & FlagFurled) != 0;
            // state.Slowdown / state.BlackDetail: deliberately untouched —
            // no iRacing telemetry source exists (see class doc).
        }
    }
}
