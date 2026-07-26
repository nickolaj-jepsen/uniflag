// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The only place SimHub types are touched on the telemetry path: copies the
// fields the mapping needs out of GameReaderCommon.GameData into the plain
// TelemetrySnapshot. Allocation-free per tick — the snapshot instance is
// reused and the only strings assigned are references the API already
// allocated.
//
// The iRacing raw object behind StatusDataBase.GetRawDataObject() is
// IRacingReader.DataSampleEx from ICarsReader.dll, a proprietary assembly NOT
// in the plugin's reference set, so it is read reflectively: one cached
// PropertyInfo fetch, then the BCL IDictionary interface (iRacingSDK.Telemetry
// derives from Dictionary<string, object>). Per-tick cost at 60 Hz is one
// field read, one PropertyInfo.GetValue and a dictionary lookup, with no
// allocation. Shapes verified against SimHub 9.11.21 and documented in
// docs/simhub-plugin-api.md.

using System;
using System.Collections.Generic;
using System.Globalization;
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
        private static PropertyInfo _rawSessionDataDictProperty;

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
            into.HasIncidentCount = false;
            into.IncidentCount = 0;
            into.HasIncidentLimit = false;
            into.IncidentLimit = 0;
            if (string.Equals(into.GameName, IRacingGameName, StringComparison.OrdinalIgnoreCase))
            {
                ExtractIRacingRaw(telemetry, into);
            }
        }

        /// <summary>
        /// Pull the iRacing raw-data signals the refiner consumes. Null-safe at
        /// every layer: any deviation from the researched shape simply leaves
        /// the corresponding <c>Has*</c> flag false, and the three reads are
        /// independent so a miss on one never skips the others. SimHub
        /// throttles plugins whose DataUpdate throws, so this path must never
        /// leak an exception.
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
                // Shape drift in a future SimHub must degrade, never throw —
                // hence SafeGetProperty, which caches null for both the absent
                // and the AmbiguousMatchException case.
                _rawTelemetryProperty = SafeGetProperty(rawType, "Telemetry");
                _rawSessionDataDictProperty = SafeGetProperty(rawType, "SessionDataDict");
                _rawType = rawType;
            }

            // iRacingSDK.Telemetry derives from Dictionary<string, object> and
            // SessionDataDict *is* one, so the BCL interface reaches both
            // without referencing the proprietary assembly.
            IDictionary<string, object> telemetryDict = ReadDictionary(raw, _rawTelemetryProperty);
            if (telemetryDict != null)
            {
                ExtractSessionFlags(telemetryDict, into);
                ExtractIncidentCount(telemetryDict, into);
            }

            IDictionary<string, object> sessionDataDict = ReadDictionary(raw, _rawSessionDataDictProperty);
            if (sessionDataDict != null)
            {
                ExtractIncidentLimit(sessionDataDict, into);
            }
        }

        /// <summary>
        /// <c>Type.GetProperty</c> that swallows the one throwing drift shape
        /// (a derived raw type shadowing the property with a different type →
        /// <c>AmbiguousMatchException</c>) and caches null like every absent
        /// property.
        /// </summary>
        private static PropertyInfo SafeGetProperty(Type type, string name)
        {
            try
            {
                return type.GetProperty(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Read a <paramref name="property"/> off <paramref name="raw"/> and
        /// return it as a string-keyed dictionary, or null on any miss
        /// (absent property, throwing getter, unexpected shape).
        /// </summary>
        private static IDictionary<string, object> ReadDictionary(object raw, PropertyInfo property)
        {
            if (property == null)
            {
                return null;
            }
            object value;
            try
            {
                value = property.GetValue(raw);
            }
            catch (Exception)
            {
                return null;
            }
            return value as IDictionary<string, object>;
        }

        /// <summary>
        /// SessionFlags bitmask. Boxed as Int32 by the reader (IL-verified:
        /// the typed getter unboxes int); tolerate unsigned/wider boxes
        /// defensively. The top bit (startGo, 0x80000000) makes the int
        /// negative — the unchecked reinterpretation preserves the bit
        /// pattern.
        /// </summary>
        private static void ExtractSessionFlags(IDictionary<string, object> dictionary, TelemetrySnapshot into)
        {
            if (!dictionary.TryGetValue("SessionFlags", out object value))
            {
                return;
            }
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

        /// <summary>
        /// PlayerCarMyIncidentCount — the player's incidents this session.
        /// A telemetry variable with no typed getter (verified: absent from
        /// iRacingSDK.Telemetry's typed members), so read straight from the
        /// dictionary. Boxed as int by the reader; tolerate wider boxes.
        /// </summary>
        private static void ExtractIncidentCount(IDictionary<string, object> dictionary, TelemetrySnapshot into)
        {
            if (dictionary.TryGetValue("PlayerCarMyIncidentCount", out object value)
                && TryReadInt(value, out int count))
            {
                into.IncidentCount = count;
                into.HasIncidentCount = true;
            }
        }

        /// <summary>
        /// The session incident limit. SimHub 9.11.21's iRacingSDK typed
        /// SessionData model omits IncidentLimit (verified by reflection over
        /// iRacingSDK.dll), so read the raw session-info tree:
        /// <c>WeekendInfo → WeekendOptions → IncidentLimit</c>. That nesting is
        /// researched, not live-verified against a running session — every
        /// layer is guarded, so a wrong nesting/key/type simply leaves
        /// <see cref="TelemetrySnapshot.HasIncidentLimit"/> false. "unlimited"
        /// (and any non-numeric value) counts as no finite limit.
        /// </summary>
        private static void ExtractIncidentLimit(IDictionary<string, object> sessionDataDict, TelemetrySnapshot into)
        {
            if (sessionDataDict.TryGetValue("WeekendInfo", out object weekendInfoObj)
                && weekendInfoObj is IDictionary<string, object> weekendInfo
                && weekendInfo.TryGetValue("WeekendOptions", out object weekendOptionsObj)
                && weekendOptionsObj is IDictionary<string, object> weekendOptions
                && weekendOptions.TryGetValue("IncidentLimit", out object value)
                && TryParseIncidentLimit(value, out int limit))
            {
                into.IncidentLimit = limit;
                into.HasIncidentLimit = true;
            }
        }

        /// <summary>Read a boxed integer value, tolerating int/long/short/uint boxes.</summary>
        private static bool TryReadInt(object value, out int result)
        {
            switch (value)
            {
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = unchecked((int)l);
                    return true;
                case short sh:
                    result = sh;
                    return true;
                case uint u:
                    result = unchecked((int)u);
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        /// <summary>
        /// Parse the IncidentLimit value: a numeric box is the limit; a string
        /// is parsed with the invariant culture ("unlimited" and any other
        /// non-numeric string → false, i.e. no finite limit).
        /// </summary>
        private static bool TryParseIncidentLimit(object value, out int limit)
        {
            switch (value)
            {
                case int i:
                    limit = i;
                    return true;
                case long l:
                    limit = unchecked((int)l);
                    return true;
                case short sh:
                    limit = sh;
                    return true;
                case string str:
                    return int.TryParse(
                        str, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
                default:
                    limit = 0;
                    return false;
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
            into.HasIncidentCount = false;
            into.IncidentCount = 0;
            into.HasIncidentLimit = false;
            into.IncidentLimit = 0;
        }
    }
}
