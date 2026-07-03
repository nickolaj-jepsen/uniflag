// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Byte-exact tests for the plugin connected-idle painter
// (plugin/src/Rendering/IdleEffects.cs) against docs/effects-spec.md §7b.
// This pattern is C#-authored — it has no golden .rgb fixture, so these
// literal RGB pins (hand-derived from the §2 primitives) are its corpus.

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class IdleEffectsTests
    {
        // Expected colours, derived by hand from the spec:
        //   m = 8 + breathe(f, 240) * 16 / 255, colour = scale_rgb((0,64,255), m)
        // trough f=180: breathe = SIN[192] = 1   → m = 8  → (0, 64*9>>8,  255*9>>8)  = (0, 2, 8)
        // peak   f=60:  breathe = SIN[64]  = 255 → m = 24 → (0, 64*25>>8, 255*25>>8) = (0, 6, 24)
        // start  f=0:   breathe = SIN[0]   = 128 → m = 16 → (0, 64*17>>8, 255*17>>8) = (0, 4, 16)
        [Theory]
        [InlineData(180u, 0, 2, 8)]
        [InlineData(60u, 0, 6, 24)]
        [InlineData(0u, 0, 4, 16)]
        public void MarkerPairIsByteExactAndEverythingElseIsBlack(uint frame, byte r, byte g, byte b)
        {
            var frameBuffer = new FrameBuffer();
            // Pre-dirty every pixel: the painter must repaint the whole
            // panel from scratch, not rely on a fresh buffer.
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    frameBuffer.SetPixel(x, y, 255, 255, 255);
                }
            }

            IdleEffects.PaintConnectedIdle(frameBuffer, frame);

            var want = new Rgb(r, g, b);
            Assert.Equal(want, frameBuffer.GetPixel(15, 31));
            Assert.Equal(want, frameBuffer.GetPixel(16, 31));
            var black = new Rgb(0, 0, 0);
            for (int y = 0; y < FrameBuffer.Height; y++)
            {
                for (int x = 0; x < FrameBuffer.Width; x++)
                {
                    if (y == 31 && (x == 15 || x == 16))
                    {
                        continue;
                    }
                    Assert.True(
                        black.Equals(frameBuffer.GetPixel(x, y)),
                        $"pixel ({x},{y}) must be black in connected-idle, got {frameBuffer.GetPixel(x, y)}");
                }
            }
        }

        [Fact]
        public void BrightnessBreathesWithinTheSpecRange()
        {
            // m ranges 8..=24 → blue channel 8..=24, green 2..=6, red 0 —
            // over a whole 240-frame period, both extremes must be hit and
            // nothing may leave the range (docs/effects-spec.md §7b).
            var frameBuffer = new FrameBuffer();
            byte minB = 255;
            byte maxB = 0;
            for (uint frame = 0; frame < 240; frame++)
            {
                IdleEffects.PaintConnectedIdle(frameBuffer, frame);
                Rgb px = frameBuffer.GetPixel(15, 31);
                Assert.Equal(px, frameBuffer.GetPixel(16, 31));
                Assert.Equal((byte)0, px.R);
                Assert.InRange(px.G, (byte)2, (byte)6);
                Assert.InRange(px.B, (byte)8, (byte)24);
                if (px.B < minB)
                {
                    minB = px.B;
                }
                if (px.B > maxB)
                {
                    maxB = px.B;
                }
            }
            Assert.Equal((byte)8, minB);
            Assert.Equal((byte)24, maxB);
        }
    }
}
