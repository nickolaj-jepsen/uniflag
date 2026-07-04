// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Text-engine unit tests: the layout math must reproduce the
// caution-board placement constants of docs/effects-spec.md §5.9 exactly
// (the 40 ported-parity goldens are the byte-level referee for the VSC/SC
// migration; these tests pin the arithmetic at the unit level so a
// placement regression names the culprit directly), plus glyph-store
// invariants and the bit-order contract of DrawGlyph.

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class TextEngineTests
    {
        [Fact]
        public void MeasureRowMatchesTheCautionBoardFormula()
        {
            // total_w = n*7 + (n-1)*gap (effects.rs:410 / spec §5.9):
            // VSC = 3 glyphs gap 1 → 23; SC = 2 glyphs gap 4 → 18.
            Assert.Equal(23, TextEngine.MeasureRow(3, 1));
            Assert.Equal(18, TextEngine.MeasureRow(2, 4));
            // Penalty rows: SLOW (4, gap 1) → 31; DT/SG (2, gap 1) → 15;
            // a single glyph ignores the gap.
            Assert.Equal(31, TextEngine.MeasureRow(4, 1));
            Assert.Equal(15, TextEngine.MeasureRow(2, 1));
            Assert.Equal(7, TextEngine.MeasureRow(1, 0));
        }

        [Fact]
        public void CenteringMatchesTheFrozenBoardPlacement()
        {
            // Centring math with the truncating division biasing odd
            // leftovers one pixel left (three glyphs gap 1 → x 4; two
            // glyphs gap 4 → x 7; glyph row → y 10).
            Assert.Equal(4, TextEngine.CenterRowX(3, 1));
            Assert.Equal(7, TextEngine.CenterRowX(2, 4));
            Assert.Equal(10, TextEngine.CenterRowY());
            // Wider and narrower rows: four glyphs gap 1 → x 0, two glyphs
            // gap 1 → x 8, one bare glyph → x 12.
            Assert.Equal(0, TextEngine.CenterRowX(4, 1));
            Assert.Equal(8, TextEngine.CenterRowX(2, 1));
            Assert.Equal(12, TextEngine.CenterRowX(1, 0));
        }

        [Fact]
        public void EveryGlyphIsElevenRowsOfSevenColumns()
        {
            byte[][] glyphs =
            {
                Font7x11.S, Font7x11.C, Font7x11.V, Font7x11.D, Font7x11.G,
                Font7x11.L, Font7x11.O, Font7x11.T, Font7x11.W,
                Font7x11.One, Font7x11.Two, Font7x11.Three,
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
            TextEngine.DrawGlyph(s, Font7x11.O, 0, 0, new Rgb(255, 255, 255));
            // O's interior (e.g. centre of row 5) is a hole: backdrop remains.
            Assert.Equal(backdrop, s.GetPixel(3, 5));
        }

        [Fact]
        public void DrawRowAdvancesGlyphWidthPlusGap()
        {
            var s = new FrameBuffer();
            var white = new Rgb(255, 255, 255);
            // Two T glyphs, gap 3: second stem starts at x = 0 + 7 + 3.
            TextEngine.DrawRow(s, new[] { Font7x11.T, Font7x11.T }, 3, 0, 0, white);
            // T row 0 is a full 7-px bar: x 0..6 and x 10..16 lit, gap dark.
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
            TextEngine.DrawGlyph(s, Font7x11.T, -4, 0, white);
            Assert.Equal(white, s.GetPixel(0, 0)); // row-0 bar, columns 4..6 visible
            Assert.Equal(white, s.GetPixel(2, 0));
            Assert.Equal(new Rgb(0, 0, 0), s.GetPixel(3, 0));
        }
    }
}
