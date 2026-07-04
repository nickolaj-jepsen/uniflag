// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The 7×11 pixel font (M10 text engine). S, C and V are moved VERBATIM from
// Effects.cs (originally render/src/effects.rs:34-77, spec §5.9) — their
// bit patterns are golden-frozen through the VSC/SC caution boards in
// testdata/frames/, so they must never change. The remaining glyphs are the
// digits and letters the penalty suite needs (SLOW, DT, SG, severity 1-3),
// drawn in the same style: 2-px strokes, rounded caps, bit 6 = leftmost
// column, one byte per row.

namespace Uniflag.Rendering
{
    /// <summary>
    /// Glyph store for the 7×11 pixel font. Each glyph is 11 row bytes; the
    /// low 7 bits of each byte are the columns, bit 6 leftmost. Only set
    /// bits are painted (the background shows through), exactly as the
    /// original caution-board glyphs behaved.
    /// </summary>
    public static class Font7x11
    {
        /// <summary>Glyph width in pixels.</summary>
        public const int GlyphWidth = 7;

        /// <summary>Glyph height in pixels (rows per glyph).</summary>
        public const int GlyphHeight = 11;

        // ---------------------------------------------------------------
        // Golden-frozen glyphs — bit-for-bit the caution-board letters of
        // render/src/effects.rs:34-77. Any change here breaks the 40
        // ported-parity goldens; fix the caller, never these tables.
        // ---------------------------------------------------------------

        /// <summary>S (frozen: caution boards, docs/effects-spec.md §5.9).</summary>
        public static readonly byte[] S =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b0111110, // .#####.
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>C (frozen: caution boards, docs/effects-spec.md §5.9).</summary>
        public static readonly byte[] C =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>V (frozen: VSC board, docs/effects-spec.md §5.9).</summary>
        public static readonly byte[] V =
        {
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0110110, // .##.##.
            0b0110110, // .##.##.
            0b0110110, // .##.##.
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0001000, // ...#...
        };

        // ---------------------------------------------------------------
        // M10 penalty-suite glyphs (C#-authored; pinned by the
        // testdata/frames-plugin/ corpus, pending maintainer visual review).
        // ---------------------------------------------------------------

        /// <summary>D (drive-through marker).</summary>
        public static readonly byte[] D =
        {
            0b1111100, // #####..
            0b1100110, // ##..##.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100110, // ##..##.
            0b1111100, // #####..
        };

        /// <summary>G (stop-and-go marker).</summary>
        public static readonly byte[] G =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1101111, // ##.####
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>L (SLOW board).</summary>
        public static readonly byte[] L =
        {
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1111111, // #######
        };

        /// <summary>O (SLOW board).</summary>
        public static readonly byte[] O =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>T (drive-through marker).</summary>
        public static readonly byte[] T =
        {
            0b1111111, // #######
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
        };

        /// <summary>W (SLOW board).</summary>
        public static readonly byte[] W =
        {
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1101011, // ##.#.##
            0b1101011, // ##.#.##
            0b1101011, // ##.#.##
            0b1101011, // ##.#.##
            0b0110110, // .##.##.
        };

        /// <summary>Digit 1 (slowdown severity).</summary>
        public static readonly byte[] One =
        {
            0b0011000, // ..##...
            0b0111000, // .###...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b1111110, // ######.
        };

        /// <summary>Digit 2 (slowdown severity).</summary>
        public static readonly byte[] Two =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000110, // ....##.
            0b0001100, // ...##..
            0b0011000, // ..##...
            0b0110000, // .##....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1111111, // #######
        };

        /// <summary>Digit 3 (slowdown severity).</summary>
        public static readonly byte[] Three =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b0000011, // .....##
            0b0000011, // .....##
            0b0011110, // ..####.
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };
    }
}
