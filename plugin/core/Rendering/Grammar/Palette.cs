// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Grammar palette (docs/flag-grammar.md §6) — the LED-tuned hues carried
// over from the legacy renderer plus the idle-family hues. Hue = identity
// (grammar rule R1): these are never repurposed.

namespace Uniflag.Rendering.Grammar
{
    public static class Palette
    {
        public static readonly Rgb Black = new Rgb(0, 0, 0);
        public static readonly Rgb Yellow = new Rgb(255, 220, 0);
        public static readonly Rgb Blue = new Rgb(0, 64, 255);
        public static readonly Rgb Red = new Rgb(255, 0, 0);
        public static readonly Rgb Green = new Rgb(0, 220, 0);
        public static readonly Rgb White = new Rgb(255, 255, 255);
        public static readonly Rgb Orange = new Rgb(255, 90, 0);
        public static readonly Rgb SectorDim = new Rgb(40, 30, 0);

        /// <summary>Idle-family hues — absent from the flag vocabulary by design.</summary>
        public static readonly Rgb Teal = new Rgb(0, 255, 192);
        public static readonly Rgb Violet = new Rgb(176, 0, 255);
        public static readonly Rgb Amber = new Rgb(255, 120, 8);

        /// <summary>Green onset-sweep band colour (docs/flag-grammar.md §6.1).</summary>
        public static readonly Rgb GreenSweepBand = new Rgb(200, 255, 200);

        /// <summary>Unlit gantry light socket.</summary>
        public static readonly Rgb GantrySocket = new Rgb(20, 20, 20);
    }
}
