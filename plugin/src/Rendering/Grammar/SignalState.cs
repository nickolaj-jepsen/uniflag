// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Grammar (second-generation signal language) state model —
// docs/flag-grammar.md §9. Adapters are pure telemetry→state functions; the
// renderer diffs successive SignalStates to run the envelope (§4), so no
// age/animation state crosses the adapter boundary. Session and SectorSet
// are shared with the parent namespace; the enums here shadow their legacy
// counterparts deliberately — the legacy model retires at phase 1e.

namespace Uniflag.Rendering.Grammar
{
    /// <summary>
    /// The winning track-state flag (adapter priority). Black and meatball
    /// are NOT here — they are orthogonal dimensions on
    /// <see cref="SignalState"/> so the demotion rule (docs/flag-grammar.md
    /// §5) can see them while another flag holds the field.
    /// </summary>
    public enum TrackFlag
    {
        None,
        Yellow,
        Blue,
        White,
        Red,
        Green,
        Checkered,
        Debris,
    }

    /// <summary>Urgency ladder (docs/flag-grammar.md §4). Replaces WaveLevel.</summary>
    public enum Tier : byte
    {
        /// <summary>Calm form: cloth-wave / slow breathe.</summary>
        Ambient = 0,

        /// <summary>Act soon: 2 Hz pulse (modulates, never cuts).</summary>
        Alert = 1,

        /// <summary>Act now: 4 Hz strobe (cuts to black).</summary>
        Urgent = 2,
    }

    /// <summary>Neutralisation regime. Shadows the legacy enum, adding FCY.</summary>
    public enum Caution
    {
        None,
        VirtualSafetyCar,
        SafetyCar,
        FullCourseYellow,
    }

    /// <summary>Black-flag service detail; meaningful only with an active black-family order.</summary>
    public enum BlackDetail
    {
        None,
        DriveThrough,
        StopAndGo,
        Disqualified,
    }

    /// <summary>Start-sequence phase (docs/flag-grammar.md §6.4).</summary>
    public enum StartPhase
    {
        Off,
        Ready,
        Set,
        Go,
    }

    /// <summary>
    /// Everything the Grammar renderer consumes besides the frame counter.
    /// Pure data — defaults mean "nothing active" so a state built without
    /// touching a dimension keeps that code path unreachable.
    /// </summary>
    public struct SignalState
    {
        /// <summary>Track-state flag; see <see cref="TrackFlag"/>.</summary>
        public TrackFlag Flag;

        /// <summary>Urgency of <see cref="Flag"/>.</summary>
        public Tier Tier;

        /// <summary>
        /// Black-family order active (with or without service detail).
        /// Orthogonal to <see cref="Flag"/> for the demotion rule.
        /// </summary>
        public bool BlackFlag;

        /// <summary>Service detail / DQ. Non-None implies a black-family order.</summary>
        public BlackDetail BlackDetail;

        /// <summary>Mechanical/meatball flag active. Orthogonal, like <see cref="BlackFlag"/>.</summary>
        public bool Meatball;

        public Session Session;
        public Caution Caution;
        public SectorSet Sectors;

        public StartPhase StartPhase;

        /// <summary>Gantry lights lit, 0..5; 0 = derive from <see cref="StartPhase"/> (LMU counts).</summary>
        public byte StartLightsLit;

        /// <summary>Time-penalty notice in seconds; 0 = none.</summary>
        public byte TimePenaltySeconds;

        /// <summary>Laps-remaining countdown notice; 0 = none (iRacing 10/5).</summary>
        public byte CountdownLaps;

        /// <summary>Furled-flag warning (frame accent).</summary>
        public bool Furled;

        /// <summary>Incident-limit warning (frame accent).</summary>
        public bool IncidentWarning;

        /// <summary>Whether any black-family order is active.</summary>
        public bool BlackActive => BlackFlag || BlackDetail != BlackDetail.None;

        public static SignalState Default => new SignalState
        {
            Session = Session.Unknown,
        };
    }
}
