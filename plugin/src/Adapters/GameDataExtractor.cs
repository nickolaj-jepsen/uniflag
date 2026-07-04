// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The only place SimHub types are touched on the telemetry path: copies the
// fields the mapping needs out of GameReaderCommon.GameData into the plain
// TelemetrySnapshot. Allocation-free per tick — the snapshot instance is
// reused and the only strings assigned are references the API already
// allocated.
//
// The iRacing raw object behind StatusDataBase.GetRawDataObject() is
// IRacingReader.DataSampleEx from ICarsReader.dll — a proprietary assembly
// NOT in the plugin's reference set (CI stages only SimHub.Plugins /
// GameReaderCommon / log4net / SimHub.Logging), so it is read reflectively:
// one cached PropertyInfo fetch of `Telemetry`, whose value derives from
// Dictionary<string, object> (iRacingSDK.Telemetry) and is therefore
// readable through the BCL IDictionary interface with no further reflection.
// Per-tick cost at 60 Hz: GetRawDataObject() is a plain field read
// (IL-verified, no boxing for the class-typed sample), one
// PropertyInfo.GetValue invocation, one dictionary lookup — no per-tick
// allocation. Shapes verified against SimHub 9.11.21; documented in
// docs/simhub-plugin-api.md.

using System;
using System.Collections.Generic;
using System.Reflection;
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
        /// <c>GameData.GameName</c> for iRacing; matches
        /// <see cref="IRacingAdapter.IRacingGameName"/>.
        /// </summary>
        private const string IRacingGameName = "IRacing";

        // Reflection cache for the raw-data shape: the concrete raw type is
        // stable for the lifetime of a game session (and plugins are
        // rebuilt at game change), so one (Type, PropertyInfo) pair
        // suffices. DataUpdate runs on SimHub's single update thread;
        // reference writes are atomic, so a racing re-resolve is at worst
        // redundant work, never a torn read.
        private static Type _rawType;
        private static PropertyInfo _rawTelemetryProperty;

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

            // Raw layer: touched ONLY when the running game is iRacing —
            // every other game keeps the generic baseline untouched and
            // pays zero raw-data cost.
            into.HasRawSessionFlags = false;
            into.RawSessionFlags = 0;
            if (string.Equals(into.GameName, IRacingGameName, StringComparison.OrdinalIgnoreCase))
            {
                ExtractIRacingRaw(telemetry, into);
            }
        }

        /// <summary>
        /// Pull the SessionFlags bitmask out of the iRacing raw-data object.
        /// Null-safe at every layer: any deviation from the researched shape
        /// (missing raw object, no <c>Telemetry</c> property, not a
        /// string-keyed dictionary, missing key, unexpected boxed type)
        /// simply leaves <see cref="TelemetrySnapshot.HasRawSessionFlags"/>
        /// false. SimHub throttles plugins whose DataUpdate throws, so this
        /// path must never leak an exception.
        /// </summary>
        private static void ExtractIRacingRaw(StatusDataBase telemetry, TelemetrySnapshot into)
        {
            object raw;
            try
            {
                raw = telemetry.GetRawDataObject();
            }
            catch (Exception)
            {
                // Abstract member implemented by SimHub's reader; guard it
                // like every other foreign call on the 60 Hz path.
                return;
            }
            if (raw == null)
            {
                return;
            }

            Type rawType = raw.GetType();
            if (!ReferenceEquals(rawType, _rawType))
            {
                // (Re-)resolve on type change. Shape drift in a future
                // SimHub must degrade, never throw: GetProperty returns
                // null when the property is gone, but it *throws*
                // AmbiguousMatchException when a derived raw type shadows
                // `Telemetry` with a different property type — cache null
                // either way, checked below.
                try
                {
                    _rawTelemetryProperty = rawType.GetProperty("Telemetry");
                }
                catch (Exception)
                {
                    _rawTelemetryProperty = null;
                }
                _rawType = rawType;
            }
            PropertyInfo telemetryProperty = _rawTelemetryProperty;
            if (telemetryProperty == null)
            {
                return;
            }

            object telemetryObject;
            try
            {
                telemetryObject = telemetryProperty.GetValue(raw);
            }
            catch (Exception)
            {
                return;
            }
            // iRacingSDK.Telemetry derives from Dictionary<string, object>,
            // so the BCL interface reaches it without referencing the
            // proprietary assembly.
            if (!(telemetryObject is IDictionary<string, object> dictionary))
            {
                return;
            }
            if (!dictionary.TryGetValue("SessionFlags", out object value))
            {
                return;
            }
            // Boxed as Int32 by the reader (IL-verified: the typed getter
            // unboxes int); tolerate unsigned/wider boxes defensively. The
            // top bit (startGo, 0x80000000) makes the int negative — the
            // unchecked reinterpretation preserves the bit pattern.
            if (value is int intValue)
            {
                into.RawSessionFlags = unchecked((uint)intValue);
                into.HasRawSessionFlags = true;
            }
            else if (value is uint uintValue)
            {
                into.RawSessionFlags = uintValue;
                into.HasRawSessionFlags = true;
            }
            else if (value is long longValue)
            {
                into.RawSessionFlags = unchecked((uint)longValue);
                into.HasRawSessionFlags = true;
            }
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
            into.HasRawSessionFlags = false;
            into.RawSessionFlags = 0;
        }
    }
}
