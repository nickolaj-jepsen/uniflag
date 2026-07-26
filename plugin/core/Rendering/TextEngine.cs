// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Layout + stamping helpers for the 7×11 pixel font. The Grammar boards
// render through these helpers: integer division everywhere, and the
// centering is tuned against that truncation — do not "improve" the
// rounding, it moves every board glyph a pixel.

namespace Uniflag.Rendering
{
    /// <summary>
    /// Measurement, centering and drawing for rows of <see cref="Font7x11"/>
    /// glyphs. Writes go through <see cref="FrameBuffer.SetPixel(int,int,Rgb)"/>,
    /// so off-panel placement clips silently like every other effect.
    /// </summary>
    public static class TextEngine
    {
        /// <summary>
        /// Width in pixels of a row of <paramref name="glyphCount"/> glyphs
        /// separated by <paramref name="gap"/> px each.
        /// </summary>
        public static int MeasureRow(int glyphCount, int gap) =>
            glyphCount * Font7x11.GlyphWidth + (glyphCount - 1) * gap;

        /// <summary>
        /// Stamp one 7×11 glyph at (<paramref name="ox"/>, <paramref name="oy"/>).
        /// Bit 6 of each row byte is the leftmost column; only set bits are
        /// painted, so the background shows through.
        /// </summary>
        public static void DrawGlyph(FrameBuffer s, byte[] glyph, int ox, int oy, Rgb color)
        {
            for (int row = 0; row < glyph.Length; row++)
            {
                byte bits = glyph[row];
                for (int col = 0; col < Font7x11.GlyphWidth; col++)
                {
                    if (((bits >> (Font7x11.GlyphWidth - 1 - col)) & 1) != 0)
                    {
                        s.SetPixel(ox + col, oy + row, color);
                    }
                }
            }
        }

        /// <summary>
        /// Stamp a row of glyphs left-to-right from (<paramref name="xLeft"/>,
        /// <paramref name="yTop"/>), advancing <c>7 + gap</c> px per glyph.
        /// </summary>
        public static void DrawRow(FrameBuffer s, byte[][] glyphs, int gap, int xLeft, int yTop, Rgb color)
        {
            for (int i = 0; i < glyphs.Length; i++)
            {
                DrawGlyph(s, glyphs[i], xLeft + i * (Font7x11.GlyphWidth + gap), yTop, color);
            }
        }
    }
}
