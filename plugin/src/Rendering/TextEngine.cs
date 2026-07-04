// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Layout + stamping helpers for the 7×11 pixel font (M10 text engine). The
// placement math here is a general re-expression of the caution-board
// letter-row math of render/src/effects.rs:409-430 — the VSC/SC boards now
// render through these helpers and MUST stay byte-identical to the frozen
// goldens in testdata/frames/ (integer division everywhere; do not
// "improve" the rounding).

namespace Uniflag.Rendering
{
    /// <summary>
    /// Measurement, centering and drawing for rows of <see cref="Font7x11"/>
    /// glyphs. All coordinates are signed and every write goes through
    /// <see cref="FrameBuffer.SetPixel(int,int,Rgb)"/>, so off-panel
    /// placement clips silently like every other effect.
    /// </summary>
    public static class TextEngine
    {
        /// <summary>
        /// Width in pixels of a row of <paramref name="glyphCount"/> glyphs
        /// separated by <paramref name="gap"/> px each — the
        /// <c>total_w = n*7 + (n-1)*gap</c> of effects.rs:410.
        /// </summary>
        public static int MeasureRow(int glyphCount, int gap) =>
            glyphCount * Font7x11.GlyphWidth + (glyphCount - 1) * gap;

        /// <summary>
        /// Left x of a horizontally centred row — <c>(32 - total_w) / 2</c>
        /// (effects.rs:411; truncating division, so odd leftovers bias one
        /// pixel left exactly as the frozen boards do).
        /// </summary>
        public static int CenterRowX(int glyphCount, int gap) =>
            (FrameBuffer.Width - MeasureRow(glyphCount, gap)) / 2;

        /// <summary>
        /// Top y of a vertically centred glyph row —
        /// <c>(32 - 11) / 2 = 10</c> (effects.rs:412).
        /// </summary>
        public static int CenterRowY() =>
            (FrameBuffer.Height - Font7x11.GlyphHeight) / 2;

        /// <summary>
        /// Stamp one 7×11 glyph at (<paramref name="ox"/>, <paramref name="oy"/>).
        /// Bit 6 of each row byte is the leftmost column; only set bits are
        /// painted, so the background shows through (effects.rs:422-430).
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
        /// <paramref name="yTop"/>), advancing <c>7 + gap</c> px per glyph —
        /// the <c>ox = x_left + i * (GLYPH_W + gap)</c> of effects.rs:414-416.
        /// </summary>
        public static void DrawRow(FrameBuffer s, byte[][] glyphs, int gap, int xLeft, int yTop, Rgb color)
        {
            for (int i = 0; i < glyphs.Length; i++)
            {
                DrawGlyph(s, glyphs[i], xLeft + i * (Font7x11.GlyphWidth + gap), yTop, color);
            }
        }

        /// <summary>
        /// Stamp a row centred both ways — exactly the caution-board letter
        /// placement (effects.rs:409-417), reused by the penalty markers.
        /// </summary>
        public static void DrawCenteredRow(FrameBuffer s, byte[][] glyphs, int gap, Rgb color) =>
            DrawRow(s, glyphs, gap, CenterRowX(glyphs.Length, gap), CenterRowY(), color);
    }
}
