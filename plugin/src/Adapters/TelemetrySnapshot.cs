// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Plain data snapshot of everything the adapter mapping consumes — no
// SimHub types, so the adapters stay unit-testable without GameReaderCommon
// in play. Filled by GameDataExtractor (the only SimHub-touching layer)
// once per DataUpdate tick; one instance is reused, never reallocated.

namespace Uniflag.Adapters
{
    /// <summary>
    /// One tick's worth of telemetry, copied out of SimHub's
    /// <c>GameData</c>. Field selection is pinned by reflection against
    /// GameReaderCommon.dll (SimHub 9.x): the unified <c>Flag_*</c>
    /// properties are <c>int</c> 0/1 on <c>StatusDataBase</c> (converted to
    /// bools here). The raw layer adds the iRacing SessionFlags bitmask, read
    /// out of <c>StatusDataBase.GetRawDataObject()</c> only while the running
    /// game is iRacing. (<c>SafetyCarActive</c> is NOT an iRacing-reader
    /// field — a binary sweep of every SimHub 9.11.21 assembly finds it in
    /// RfactorReader.dll only, per docs/simhub-flag-properties.md; iRacing's
    /// pace car is detected from raw data instead.)
    /// </summary>
    public sealed class TelemetrySnapshot
    {
        /// <summary>Mirror of <c>GameData.GameRunning</c>.</summary>
        public bool GameRunning { get; set; }

        /// <summary>Mirror of <c>GameData.GameInMenu</c>.</summary>
        public bool GameInMenu { get; set; }

        /// <summary>Mirror of <c>GameData.GamePaused</c>.</summary>
        public bool GamePaused { get; set; }

        /// <summary>Whether <c>GameData.NewData</c> was non-null this tick.</summary>
        public bool HasData { get; set; }

        /// <summary>Mirror of <c>GameData.GameName</c> — the adapter-pipeline routing key.</summary>
        public string GameName { get; set; }

        /// <summary>Mirror of <c>StatusDataBase.SessionTypeName</c> ("Race", "Practice", …).</summary>
        public string SessionTypeName { get; set; }

        /// <summary>Unified <c>Flag_Yellow</c> (non-zero → set).</summary>
        public bool FlagYellow { get; set; }

        /// <summary>Unified <c>Flag_Blue</c>.</summary>
        public bool FlagBlue { get; set; }

        /// <summary>Unified <c>Flag_Black</c>.</summary>
        public bool FlagBlack { get; set; }

        /// <summary>Unified <c>Flag_White</c>.</summary>
        public bool FlagWhite { get; set; }

        /// <summary>Unified <c>Flag_Checkered</c>.</summary>
        public bool FlagCheckered { get; set; }

        /// <summary>Unified <c>Flag_Green</c>.</summary>
        public bool FlagGreen { get; set; }

        /// <summary>Unified <c>Flag_Orange</c>.</summary>
        public bool FlagOrange { get; set; }

        /// <summary>
        /// Whether <see cref="RawSessionFlags"/> holds a live value this
        /// tick. Only ever true while the running game is iRacing and the
        /// raw-data object exposed the expected
        /// <c>DataSampleEx.Telemetry["SessionFlags"]</c> shape — any missing
        /// or unexpected layer leaves this false, never throws.
        /// </summary>
        public bool HasRawSessionFlags { get; set; }

        /// <summary>
        /// iRacing <c>irsdk_Flags</c> SessionFlags bitmask, raw from
        /// <c>GetRawDataObject()</c>. Bit values are pinned in
        /// <see cref="IRacingAdapter"/> (verified against the iRacingSDK.dll
        /// shipped inside SimHub 9.11.21). Meaningless unless
        /// <see cref="HasRawSessionFlags"/> is true.
        /// </summary>
        public uint RawSessionFlags { get; set; }

        /// <summary>
        /// Whether <see cref="IncidentCount"/> holds a live value this tick.
        /// Only true while the running game is iRacing and the raw telemetry
        /// exposed a <c>PlayerCarMyIncidentCount</c> entry — any missing layer
        /// leaves it false, never throws.
        /// </summary>
        public bool HasIncidentCount { get; set; }

        /// <summary>
        /// The player's incident count this session (iRacing telemetry
        /// <c>PlayerCarMyIncidentCount</c>). Meaningless unless
        /// <see cref="HasIncidentCount"/> is true.
        /// </summary>
        public int IncidentCount { get; set; }

        /// <summary>
        /// Whether <see cref="IncidentLimit"/> holds a finite limit this tick.
        /// False when the running game is not iRacing, the session-info
        /// dictionary was unavailable/an unexpected shape, or the limit is
        /// "unlimited"/non-numeric (in which case there is nothing to warn
        /// against). See <c>GameDataExtractor.ExtractIRacingIncidentLimit</c>.
        /// </summary>
        public bool HasIncidentLimit { get; set; }

        /// <summary>
        /// The session incident limit (iRacing session-info
        /// <c>WeekendInfo:WeekendOptions:IncidentLimit</c>). Meaningless unless
        /// <see cref="HasIncidentLimit"/> is true.
        /// </summary>
        public int IncidentLimit { get; set; }

        /// <summary>
        /// The no-game predicate (docs/effects-spec.md §7b trigger): a live
        /// game session needs the game process running, the player out of
        /// the menus, and a telemetry block present. <c>GamePaused</c>
        /// deliberately still counts as live — a paused session maps to
        /// <c>Session.Paused</c> and renders the race-idle marker, not the
        /// connected-idle marker (the game is there, just held).
        /// </summary>
        public bool HasLiveSession => GameRunning && !GameInMenu && HasData;
    }
}
