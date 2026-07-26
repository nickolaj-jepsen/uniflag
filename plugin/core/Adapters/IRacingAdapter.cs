// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The iRacing raw-telemetry refiner: layered after GenericAdapter, it
// overrides or enriches ONLY where the raw SessionFlags bitmask is better
// than the unified Flag_* layer. Every bit value below was verified against
// the iRacingSDK.dll shipped inside SimHub 9.11.21 (the assembly SimHub's
// own reader consumes), and SimHub's unified mapping was IL-verified for
// cross-checking. docs/simhub-flag-properties.md records that research — what
// the sim exposes and how it was established; this file is the mapping.

using System;
using Uniflag.Rendering.Grammar;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Refines the generic mapping with iRacing raw data
    /// (docs/flag-grammar.md §10). Pure and allocation-free per call (~60 Hz).
    ///
    /// <para>Checkered / white / green / black are single raw bits the unified
    /// layer already mirrors 1:1 (IL-verified), so they are left alone. Blue
    /// is <b>not</b> a pure mirror: SimHub derives
    /// <c>Flag_Blue = blue &amp;&amp; !green</c>, its fix for the spurious
    /// start-window blues of SimHub issue #436. This adapter deliberately does
    /// <b>not</b> restore blue from raw during that overlap — green is the
    /// flag that matters at a start/restart, and honouring the suppression
    /// keeps the panel consistent with every other SimHub-driven display.</para>
    ///
    /// <para>One absence worth knowing: iRacing exposes no DT-vs-SG
    /// distinction in telemetry (verified), so a bare <c>black</c> bit stays
    /// a bare black flag.</para>
    /// </summary>
    public static class IRacingAdapter
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
        internal const uint FlagStartHidden = 0x10000000;
        internal const uint FlagStartReady = 0x20000000;
        internal const uint FlagStartSet = 0x40000000;
        internal const uint FlagStartGo = 0x80000000;

        /// <summary>
        /// Warn once the player's session incident count is within this many
        /// of the session limit. A single hard incident is 4 points, so a
        /// margin of 4 gives roughly one-incident heads-up. Tunable.
        /// </summary>
        internal const int IncidentWarnMargin = 4;

        public static bool Matches(string gameName) =>
            string.Equals(gameName, IRacingGameName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Refine the generic verdict in <paramref name="state"/> with raw
        /// iRacing telemetry. Runs after <see cref="GenericAdapter.Map"/>.
        /// </summary>
        public static void Map(TelemetrySnapshot snapshot, ref SignalState state)
        {
            // Incident-limit warning is independent of the SessionFlags mask
            // (it reads the incident count from telemetry and the limit from
            // the session-info dictionary), so derive it before the mask
            // guard — a tick that lost the mask can still warn. Only fires
            // when both are known and the limit is finite (>0; an "unlimited"
            // limit leaves HasIncidentLimit false in the extractor).
            //
            // It renders as the red perimeter frame, which the compositor
            // suppresses under a red/checkered takeover and under DQ — so
            // raising it here does not guarantee it is visible.
            if (snapshot.HasIncidentCount && snapshot.HasIncidentLimit && snapshot.IncidentLimit > 0)
            {
                state.IncidentWarning =
                    snapshot.IncidentCount >= snapshot.IncidentLimit - IncidentWarnMargin;
            }

            if (!snapshot.HasRawSessionFlags)
            {
                // Raw layer unavailable this tick (shape drift, no sample
                // yet): the generic unified result stands untouched.
                return;
            }
            uint bits = snapshot.RawSessionFlags;

            // Red — raw-only. Session stopped; maximum urgency.
            if ((bits & FlagRed) != 0)
            {
                state.Flag = TrackFlag.Red;
                state.Tier = Tier.Urgent;
            }

            // Full-course caution → SC board (pace car; no VSC in iRacing).
            if ((bits & (FlagCaution | FlagCautionWaving)) != 0)
            {
                state.SafetyCar = true;
            }

            // Tier refinement (never fabricate a flag here — SimHub's
            // unified Flag_Yellow already ORs the yellow AND caution bits,
            // so the generic adapter owns the flag choice). Raw kills the
            // generic waving guess for displayed-only yellows.
            if (state.Flag == TrackFlag.Yellow)
            {
                state.Tier = (bits & FlagCautionWaving) != 0 ? Tier.Urgent
                    : (bits & (FlagYellowWaving | FlagCaution)) != 0 ? Tier.Alert
                    : Tier.Ambient;
            }
            else if (state.Flag == TrackFlag.Blue || state.Flag == TrackFlag.Green)
            {
                state.Tier = Tier.Alert;
            }

            // Disqualify: the black-family order, orthogonal to the track
            // flag — the compositor's demotion rule keeps it visible even
            // while another flag holds the field.
            if ((bits & FlagDisqualify) != 0)
            {
                state.BlackFlag = true;
                state.Disqualified = true;
            }

            // Penalty dimensions. repair also sets unified Flag_Orange, so
            // the generic adapter already raised Meatball — this just keeps
            // raw and unified in lockstep. A bare black bit stays a bare
            // black flag (see class doc).
            state.Meatball = (bits & FlagRepair) != 0;
            state.Furled = (bits & FlagFurled) != 0;

            // Debris — lowest track state; only when nothing else won.
            if (state.Flag == TrackFlag.None && (bits & FlagDebris) != 0)
            {
                state.Flag = TrackFlag.Debris;
                state.Tier = Tier.Ambient;
            }

            // Countdown notices (the 10/5 boards).
            state.CountdownLaps = (bits & FlagTenToGo) != 0 ? (byte)10
                : (bits & FlagFiveToGo) != 0 ? (byte)5
                : (byte)0;

            // Start-sequence gantry. iRacing has no light counts.
            state.StartPhase = MapStartPhase(bits);
        }

        /// <summary>
        /// Standing/rolling start phase from the start-light bits: go &gt; set
        /// &gt; ready, with the rolling-start <c>oneLapToGreen</c> folded into
        /// Ready ("get ready, green next lap") and <c>greenHeld</c> folded
        /// into Set (furled green in the starter's hand — green imminent).
        /// <c>startHidden</c> and the unset case are
        /// <see cref="StartPhase.Off"/>.
        ///
        /// <para><c>greenHeld</c> is deliberately <b>not</b> mapped to a green
        /// flag. iRacing raises it while the starter is still holding the
        /// green <i>furled</i>, so surfacing it as green made the panel jump
        /// the start. The panel goes green only when the <c>green</c> bit
        /// itself flies.</para>
        /// </summary>
        private static StartPhase MapStartPhase(uint bits)
        {
            if ((bits & FlagStartGo) != 0)
            {
                return StartPhase.Go;
            }
            if ((bits & (FlagStartSet | FlagGreenHeld)) != 0)
            {
                return StartPhase.Set;
            }
            if ((bits & (FlagStartReady | FlagOneLapToGreen)) != 0)
            {
                return StartPhase.Ready;
            }
            return StartPhase.Off;
        }
    }
}
