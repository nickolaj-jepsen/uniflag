// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The precedence ladder as its own dispatch layer (docs/effects-spec.md §4).
// Kept separate from the painters so it can be unit-tested in isolation:
//
//   disconnected > red > VSC > SC > slowdown > meatball
//                > per-flag base > start-lights > debris > session idle
//
// with three overlays painted last unless the flag is red: the sector band,
// the furled warning accent and the incident-limit warning frame. Rationale
// for the penalty positions: red means the session is stopped — penalties
// are moot; the caution boards are a full-course neutralisation order and
// penalty service waits for the pits anyway; slowdown outranks meatball
// because it demands an immediate speed change while the meatball is "pit at
// the end of this lap"; both outrank per-flag bases because they are orders
// directed at this driver, not track state. DT/SG are variants of the
// black-flag base (RenderState.BlackDetail), not separate layers; furled and
// the incident warning are accents, not boards.
//
// Start-lights and debris sit in the session-idle region — they only paint
// when no flag/caution/penalty claimed the base (F=None). A start gantry is
// only meaningful before green (F=None during the grid/pace hold), and at
// "GO" the green flag rightly supersedes it; a debris warning is the lowest
// track-state signal, so any real flag (which already conveys caution) wins.
// This keeps them from ever overriding a driver-directed flag, and — being
// reachable only for non-default StartLights/Debris — leaves every existing
// (state, frame) tuple dispatching exactly as the frozen goldens pin it.

namespace Uniflag.Rendering
{
    /// <summary>The base layer <see cref="Effects.Paint"/> renders before any overlay.</summary>
    public enum RenderLayer
    {
        /// <summary>Host silent past the timeout: blank panel, no overlays.</summary>
        Disconnected,

        /// <summary>Red flag — beats caution and everything else.</summary>
        RedFlag,

        /// <summary>VSC board — beats every flag except red.</summary>
        VscBoard,

        /// <summary>Safety-car board — beats every flag except red.</summary>
        SafetyCarBoard,

        /// <summary>Slow-down penalty board — beats every per-flag base, loses to caution boards.</summary>
        SlowdownBoard,

        /// <summary>Meatball / mandatory-repair board — beats every per-flag base, loses to slowdown.</summary>
        MeatballBoard,

        YellowFlag,
        BlueFlag,
        GreenFlag,
        WhiteFlag,
        BlackFlag,
        OrangeFlag,
        CheckeredFlag,

        /// <summary>No flag: start-light gantry (iRacing start sequence).</summary>
        StartLightsBoard,

        /// <summary>No flag: debris / surface warning board.</summary>
        DebrisBoard,

        /// <summary>No flag, session Racing/Paused: minimal alive marker.</summary>
        RaceIdle,

        /// <summary>No flag, any other session: ready orb (5 s fallback to race idle).</summary>
        ReadyOrb,
    }

    /// <summary>Pure dispatch: which layer to paint, and which overlays sit on top of it.</summary>
    public static class Precedence
    {
        /// <summary>
        /// Select the base layer for <paramref name="state"/>, with the
        /// penalty boards slotted between the caution boards and the per-flag
        /// bases. Default penalty state never reaches those arms, so every
        /// penalty-free (state, frame) tuple dispatches as the 40 ported-parity
        /// goldens pin it.
        /// </summary>
        public static RenderLayer Select(RenderState state, bool connected)
        {
            if (!connected)
            {
                return RenderLayer.Disconnected;
            }
            if (state.Flag == Flag.Red)
            {
                return RenderLayer.RedFlag;
            }
            if (state.Caution == Caution.VirtualSafetyCar)
            {
                return RenderLayer.VscBoard;
            }
            if (state.Caution == Caution.SafetyCar)
            {
                return RenderLayer.SafetyCarBoard;
            }
            if (state.Slowdown != 0)
            {
                return RenderLayer.SlowdownBoard;
            }
            if (state.Meatball)
            {
                return RenderLayer.MeatballBoard;
            }
            switch (state.Flag)
            {
                case Flag.Yellow:
                    return RenderLayer.YellowFlag;
                case Flag.Blue:
                    return RenderLayer.BlueFlag;
                case Flag.Green:
                    return RenderLayer.GreenFlag;
                case Flag.White:
                    return RenderLayer.WhiteFlag;
                case Flag.Black:
                    return RenderLayer.BlackFlag;
                case Flag.Orange:
                    return RenderLayer.OrangeFlag;
                case Flag.Checkered:
                    return RenderLayer.CheckeredFlag;
                default: // Flag.None
                    // Start-lights and debris slot in ahead of the idle
                    // markers (both default to "off", so the existing idle
                    // dispatch is unchanged). Start-lights first: a start
                    // gantry outranks a debris warning in the rare overlap.
                    if (state.StartLights != StartLights.Off)
                    {
                        return RenderLayer.StartLightsBoard;
                    }
                    if (state.Debris)
                    {
                        return RenderLayer.DebrisBoard;
                    }
                    return state.Session == Session.Racing || state.Session == Session.Paused
                        ? RenderLayer.RaceIdle
                        : RenderLayer.ReadyOrb;
            }
        }

        /// <summary>
        /// Whether the sector band overlay is painted on top of the base
        /// layer: never when disconnected, never under red (drivers must
        /// stop — extra signalling is noise), and only when at least one
        /// sector is flagged. Caution and penalty boards keep it (still
        /// racing, sector state still matters).
        /// </summary>
        public static bool SectorBandVisible(RenderState state, bool connected) =>
            connected && !state.Sectors.IsEmpty && state.Flag != Flag.Red;

        /// <summary>
        /// Whether the furled warning accent is painted on top of the base
        /// layer. Mirrors the sector-band rule: never disconnected, never
        /// under red, otherwise whenever the warning is set — over flags,
        /// boards and idle alike (a warning stays a warning).
        /// </summary>
        public static bool FurledAccentVisible(RenderState state, bool connected) =>
            connected && state.Furled && state.Flag != Flag.Red;

        /// <summary>
        /// Whether the incident-limit warning frame is painted on top of the
        /// base layer. Same rule as the furled accent (never disconnected,
        /// never under red, otherwise whenever set): a heads-up that rides
        /// over whatever the driver is currently being shown while racing
        /// continues.
        /// </summary>
        public static bool IncidentWarningVisible(RenderState state, bool connected) =>
            connected && state.IncidentWarning && state.Flag != Flag.Red;
    }
}
