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
    /// bools here); <c>SafetyCarActive</c> is <b>not</b> a typed member of
    /// <c>StatusDataBase</c> (it only exists in the property bag, populated
    /// by the iRacing reader) so it cannot be captured until the M10 raw
    /// adapter reads game-specific data.
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
