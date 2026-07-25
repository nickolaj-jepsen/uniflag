// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The 7×11 pixel font, carried over from the ported renderer and rendered
// through the Grammar boards. All glyphs share one style: 2-px strokes,
// rounded caps, bit 6 = leftmost column, one byte per row — match it when
// adding one, or the new glyph reads as a different typeface on the panel.

namespace Uniflag.Rendering
{
    /// <summary>
    /// Glyph store for the 7×11 pixel font. Each glyph is 11 row bytes; the
    /// low 7 bits of each byte are the columns, bit 6 leftmost. Only set bits
    /// are painted (the background shows through).
    /// </summary>
    public static class Font7x11
    {
        public const int GlyphWidth = 7;

        public const int GlyphHeight = 11;

        // ---------------------------------------------------------------
        // Caution-board glyphs (S/C/V), carried over verbatim from the
        // ported renderer; pinned by the Grammar corpus.
        // ---------------------------------------------------------------

        /// <summary>S (caution boards, docs/effects-spec.md §5.9).</summary>
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

        /// <summary>C (caution boards, docs/effects-spec.md §5.9).</summary>
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

        /// <summary>V (VSC board, docs/effects-spec.md §5.9).</summary>
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
        // Letter glyphs from the retired penalty suite. D/G/T live on in the
        // Grammar boards (DT/SG/DQ); L/O/W are currently unused but kept —
        // they exercise the text engine's row layout in tests.
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

        // ---------------------------------------------------------------
        // Grammar-suite glyphs (docs/flag-grammar.md §8). Same style: 2-px
        // strokes, bit 6 = leftmost column.
        // ---------------------------------------------------------------

        /// <summary>F (FCY board).</summary>
        public static readonly byte[] F =
        {
            0b1111111, // #######
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1111110, // ######.
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
        };

        /// <summary>Y (FCY board).</summary>
        public static readonly byte[] Y =
        {
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0110110, // .##.##.
            0b0110110, // .##.##.
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
        };

        /// <summary>Q (DQ board).</summary>
        public static readonly byte[] Q =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1101011, // ##.#.##
            0b1100110, // ##..##.
            0b0111011, // .###.##
            0b0000001, // ......#
        };

        /// <summary>X (demoted black-flag board).</summary>
        public static readonly byte[] X =
        {
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0110110, // .##.##.
            0b0110110, // .##.##.
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0110110, // .##.##.
            0b0110110, // .##.##.
            0b1100011, // ##...##
            0b1100011, // ##...##
        };

        /// <summary>+ (time-penalty boards).</summary>
        public static readonly byte[] Plus =
        {
            0b0000000, // .......
            0b0000000, // .......
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b1111111, // #######
            0b1111111, // #######
            0b1111111, // #######
            0b0011100, // ..###..
            0b0011100, // ..###..
            0b0000000, // .......
            0b0000000, // .......
        };

        /// <summary>Digit 0 (notice boards).</summary>
        public static readonly byte[] Zero =
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

        /// <summary>Digit 4 (notice boards).</summary>
        public static readonly byte[] Four =
        {
            0b0000110, // ....##.
            0b0001110, // ...###.
            0b0011110, // ..####.
            0b0110110, // .##.##.
            0b1100110, // ##..##.
            0b1100110, // ##..##.
            0b1111111, // #######
            0b0000110, // ....##.
            0b0000110, // ....##.
            0b0000110, // ....##.
            0b0000110, // ....##.
        };

        /// <summary>Digit 5 (notice boards).</summary>
        public static readonly byte[] Five =
        {
            0b1111111, // #######
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1111110, // ######.
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>Digit 6 (notice boards).</summary>
        public static readonly byte[] Six =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100000, // ##.....
            0b1100000, // ##.....
            0b1111110, // ######.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>Digit 7 (notice boards).</summary>
        public static readonly byte[] Seven =
        {
            0b1111111, // #######
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000110, // ....##.
            0b0000110, // ....##.
            0b0001100, // ...##..
            0b0001100, // ...##..
            0b0011000, // ..##...
            0b0011000, // ..##...
            0b0110000, // .##....
            0b0110000, // .##....
        };

        /// <summary>Digit 8 (notice boards).</summary>
        public static readonly byte[] Eight =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };

        /// <summary>Digit 9 (notice boards).</summary>
        public static readonly byte[] Nine =
        {
            0b0111110, // .#####.
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b1100011, // ##...##
            0b0111111, // .######
            0b0000011, // .....##
            0b0000011, // .....##
            0b0000011, // .....##
            0b1100011, // ##...##
            0b0111110, // .#####.
        };
    }
}
