// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Text-engine unit tests: board placement arithmetic, glyph-store invariants
// and the bit-order contract of DrawGlyph. Pinned at the unit level so a
// placement regression names the culprit directly.

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class TextEngineTests
    {
        [Fact]
        public void MeasureRowMatchesTheCautionBoardFormula()
        {
            // total_w = n*7 + (n-1)*gap (docs/flag-grammar.md §8):
            // three glyphs gap 1 → 23; two glyphs gap 4 → 18.
            Assert.Equal(23, TextEngine.MeasureRow(3, 1));
            Assert.Equal(18, TextEngine.MeasureRow(2, 4));
            // SC / DQ (2, gap 1) → 15; a single glyph ignores the gap.
            Assert.Equal(31, TextEngine.MeasureRow(4, 1));
            Assert.Equal(15, TextEngine.MeasureRow(2, 1));
            Assert.Equal(7, TextEngine.MeasureRow(1, 0));
        }

        [Fact]
        public void EveryGlyphIsElevenRowsOfSevenColumns()
        {
            // The whole surviving inventory (docs/flag-grammar.md §8).
            byte[][] glyphs =
            {
                Font7x11.S, Font7x11.C, Font7x11.D, Font7x11.Q, Font7x11.X,
                Font7x11.Zero, Font7x11.One, Font7x11.Two, Font7x11.Three, Font7x11.Four,
                Font7x11.Five, Font7x11.Six, Font7x11.Seven, Font7x11.Eight, Font7x11.Nine,
            };
            foreach (byte[] glyph in glyphs)
            {
                Assert.Equal(Font7x11.GlyphHeight, glyph.Length);
                foreach (byte row in glyph)
                {
                    // Bit 7 must never be set — only the low 7 bits are columns.
                    Assert.True(row < 0x80, $"glyph row 0b{System.Convert.ToString(row, 2)} uses more than 7 columns");
                }
            }
        }

        [Fact]
        public void DrawGlyphStampsBitSixAsTheLeftmostColumn()
        {
            var s = new FrameBuffer();
            var white = new Rgb(255, 255, 255);
            // S row 0 is 0b0111110 (.#####.): column 0 and 6 clear, 1..5 set.
            TextEngine.DrawGlyph(s, Font7x11.S, 3, 5, white);
            Assert.Equal(new Rgb(0, 0, 0), s.GetPixel(3, 5));
            for (int col = 1; col <= 5; col++)
            {
                Assert.Equal(white, s.GetPixel(3 + col, 5));
            }
            Assert.Equal(new Rgb(0, 0, 0), s.GetPixel(9, 5));
        }

        [Fact]
        public void DrawGlyphLeavesUnsetBitsUntouched()
        {
            // Only set bits paint — the background shows through, exactly
            // like the original caution-board stamping.
            var s = new FrameBuffer();
            var backdrop = new Rgb(10, 20, 30);
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    s.SetPixel(x, y, backdrop);
                }
            }
            TextEngine.DrawGlyph(s, Font7x11.Zero, 0, 0, new Rgb(255, 255, 255));
            // 0's interior (e.g. centre of row 5) is a hole: backdrop remains.
            Assert.Equal(backdrop, s.GetPixel(3, 5));
        }

        [Fact]
        public void DrawRowAdvancesGlyphWidthPlusGap()
        {
            var s = new FrameBuffer();
            var white = new Rgb(255, 255, 255);
            // Two 7 glyphs, gap 3: the second starts at x = 0 + 7 + 3.
            TextEngine.DrawRow(s, new[] { Font7x11.Seven, Font7x11.Seven }, 3, 0, 0, white);
            // 7's row 0 is a full 7-px bar: x 0..6 and x 10..16 lit, gap dark.
            Assert.Equal(white, s.GetPixel(6, 0));
            Assert.Equal(new Rgb(0, 0, 0), s.GetPixel(8, 0));
            Assert.Equal(white, s.GetPixel(10, 0));
            Assert.Equal(white, s.GetPixel(16, 0));
        }

        [Fact]
        public void OffPanelPlacementClipsSilently()
        {
            // Signed coordinates clip like every effect write — stamping a
            // glyph half off the left edge must not throw and must paint
            // the visible half.
            var s = new FrameBuffer();
            var white = new Rgb(255, 255, 255);
            TextEngine.DrawGlyph(s, Font7x11.Seven, -4, 0, white);
            Assert.Equal(white, s.GetPixel(0, 0)); // row-0 bar, columns 4..6 visible
            Assert.Equal(white, s.GetPixel(2, 0));
            Assert.Equal(new Rgb(0, 0, 0), s.GetPixel(3, 0));
        }
    }
}
