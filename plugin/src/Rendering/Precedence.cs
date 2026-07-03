// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The precedence ladder as its own dispatch layer (docs/effects-spec.md §4,
// mirroring the match in render/src/effects.rs:79-112 and the authoritative
// ladder in firmware/src/runtime.rs:39-59). Kept separate from the painters
// so it can be unit-tested in isolation:
//
//   disconnected > red > VSC > SC > per-flag base > session idle
//
// with the sector band overlaid last unless the flag is red.

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

        YellowFlag,
        BlueFlag,
        GreenFlag,
        WhiteFlag,
        BlackFlag,
        OrangeFlag,
        CheckeredFlag,

        /// <summary>No flag, session Racing/Paused: minimal alive marker.</summary>
        RaceIdle,

        /// <summary>No flag, any other session: ready orb (5 s fallback to race idle).</summary>
        ReadyOrb,
    }

    /// <summary>Pure dispatch: which layer to paint, and whether the sector band overlays it.</summary>
    public static class Precedence
    {
        /// <summary>
        /// Select the base layer for <paramref name="state"/> — the C# mirror
        /// of the <c>match (state.flag, state.caution)</c> dispatch in
        /// <c>effects.rs:87-105</c> plus the disconnected early-out.
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
                    return state.Session == Session.Racing || state.Session == Session.Paused
                        ? RenderLayer.RaceIdle
                        : RenderLayer.ReadyOrb;
            }
        }

        /// <summary>
        /// Whether the sector band overlay is painted on top of the base
        /// layer (<c>effects.rs:109-111</c>): never when disconnected, never
        /// under red (drivers must stop — extra signalling is noise), and
        /// only when at least one sector is flagged. Caution boards keep it.
        /// </summary>
        public static bool SectorBandVisible(RenderState state, bool connected) =>
            connected && !state.Sectors.IsEmpty && state.Flag != Flag.Red;
    }
}
