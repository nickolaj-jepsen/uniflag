// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The only place SimHub types are touched on the telemetry path: copies the
// fields the mapping needs out of GameReaderCommon.GameData into the plain
// TelemetrySnapshot. Allocation-free per tick — the snapshot instance is
// reused and the only strings assigned are references the API already
// allocated.

using GameReaderCommon;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Fills a <see cref="TelemetrySnapshot"/> from <c>ref GameData</c>.
    /// Everything is copied by value (or string reference) — nothing
    /// retains <paramref name="data"/> or its <c>NewData</c> block past the
    /// call, honouring SimHub's ref-only lending of the update payload.
    /// </summary>
    public static class GameDataExtractor
    {
        /// <summary>
        /// Copy one tick. A null <paramref name="data"/> or a null
        /// <c>NewData</c> telemetry block clears the snapshot's per-session
        /// fields so a stale previous tick can never leak through
        /// (<see cref="TelemetrySnapshot.HasData"/> gates the mapping
        /// anyway, but the snapshot must not lie).
        /// </summary>
        public static void Extract(ref GameData data, TelemetrySnapshot into)
        {
            if (into == null)
            {
                throw new System.ArgumentNullException(nameof(into));
            }
            if (data == null)
            {
                into.GameRunning = false;
                into.GameInMenu = false;
                into.GamePaused = false;
                into.GameName = null;
                ClearSessionFields(into);
                return;
            }

            into.GameRunning = data.GameRunning;
            into.GameInMenu = data.GameInMenu;
            into.GamePaused = data.GamePaused;
            into.GameName = data.GameName;

            StatusDataBase telemetry = data.NewData;
            if (telemetry == null)
            {
                ClearSessionFields(into);
                return;
            }

            into.HasData = true;
            into.SessionTypeName = telemetry.SessionTypeName;
            // Unified flags are int 0/1 on StatusDataBase (verified by
            // reflection against GameReaderCommon.dll) — normalize to bool.
            into.FlagYellow = telemetry.Flag_Yellow != 0;
            into.FlagBlue = telemetry.Flag_Blue != 0;
            into.FlagBlack = telemetry.Flag_Black != 0;
            into.FlagWhite = telemetry.Flag_White != 0;
            into.FlagCheckered = telemetry.Flag_Checkered != 0;
            into.FlagGreen = telemetry.Flag_Green != 0;
            into.FlagOrange = telemetry.Flag_Orange != 0;
        }

        private static void ClearSessionFields(TelemetrySnapshot into)
        {
            into.HasData = false;
            into.SessionTypeName = null;
            into.FlagYellow = false;
            into.FlagBlue = false;
            into.FlagBlack = false;
            into.FlagWhite = false;
            into.FlagCheckered = false;
            into.FlagGreen = false;
            into.FlagOrange = false;
        }
    }
}
