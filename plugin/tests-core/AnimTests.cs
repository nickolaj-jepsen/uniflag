// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Unit tests for the animation primitives (plugin/core/Rendering/Anim.cs).
// The painter tests exercise these transitively; the anchors here make a
// primitive regression fail with a readable message instead of a pile of
// unexplained pixel diffs.

using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class SinLutTests
    {
        // Anchors of the integer Bhaskara I formula.
        [Theory]
        [InlineData(0, 128)]
        [InlineData(1, 131)]
        [InlineData(63, 254)]
        [InlineData(64, 255)]
        [InlineData(65, 254)]
        [InlineData(128, 128)]
        [InlineData(192, 1)]
        [InlineData(255, 125)]
        public void AnchorEntriesMatchTheSpec(int index, int expected)
        {
            Assert.Equal((byte)expected, Anim.SinU8[index]);
        }

        [Fact]
        public void PeakAndTroughAreSingleSamples()
        {
            Assert.Equal(256, Anim.SinU8.Length);
            int peaks = 0;
            int troughs = 0;
            for (int k = 0; k < 256; k++)
            {
                byte v = Anim.SinU8[k];
                Assert.InRange(v, (byte)1, (byte)255); // trough is 1, never 0
                if (v == 255)
                {
                    peaks++;
                    Assert.Equal(64, k);
                }
                if (v == 1)
                {
                    troughs++;
                    Assert.Equal(192, k);
                }
            }
            Assert.Equal(1, peaks);
            Assert.Equal(1, troughs);
        }

        [Fact]
        public void HalvesAreComplementaryAroundTheCentre()
        {
            // By construction: SIN[k] = 128 + mag(a), SIN[k + 128] = 128 - mag(a).
            for (int k = 0; k < 128; k++)
            {
                Assert.Equal(256, Anim.SinU8[k] + Anim.SinU8[k + 128]);
            }
        }
    }

    public class Strobe60Tests
    {
        // The exact (period * 6 + 5) / 10 duty rounding, sampled at each edge.
        [Theory]
        // hz=2: period 30, on 18
        [InlineData(2, 0, true)]
        [InlineData(2, 17, true)]
        [InlineData(2, 18, false)]
        [InlineData(2, 29, false)]
        [InlineData(2, 30, true)]
        // hz=3: period 20, on 12
        [InlineData(3, 11, true)]
        [InlineData(3, 12, false)]
        [InlineData(3, 19, false)]
        // hz=4: period 15, on 9
        [InlineData(4, 8, true)]
        [InlineData(4, 9, false)]
        [InlineData(4, 20, true)] // 20 % 15 = 5 < 9 — discriminates 4 Hz from 2 Hz
        // hz=5: period 12, on 7
        [InlineData(5, 6, true)]
        [InlineData(5, 7, false)]
        [InlineData(5, 13, true)] // 13 % 12 = 1 < 7
        public void MatchesTheDerivationTable(int hz, int frame, bool expectedOn)
        {
            Assert.Equal(expectedOn, Anim.Strobe60((uint)frame, (uint)hz));
        }
    }

    public class BreatheTests
    {
        [Theory]
        // Period 240 (race idle / caution border): peak at 60, trough at 180.
        [InlineData(0, 240, 128)]
        [InlineData(60, 240, 255)]
        [InlineData(120, 240, 128)]
        [InlineData(180, 240, 1)]
        // Period 120 (ready orb): peak at 30.
        [InlineData(30, 120, 255)]
        // Period 100 (black flag): frame 25 → LUT index 64 → peak.
        [InlineData(25, 100, 255)]
        // Wraps with the frame counter.
        [InlineData(240, 240, 128)]
        public void EnvelopeHitsTheDerivedExtremes(int frame, int period, int expected)
        {
            Assert.Equal((byte)expected, Anim.Breathe((uint)frame, (uint)period));
        }
    }

    public class WaveMultTests
    {
        [Fact]
        public void PhaseZeroMapsMidLut()
        {
            // phase 0 → SIN[0] = 128 → 150 + 128*105/255 = 202.
            Assert.Equal((byte)202, Anim.WaveMult(0, 0, 0, 150, 255));
        }

        [Fact]
        public void PhaseWrapsAtOneByte()
        {
            // 16x with x=16 is 256 ≡ 0 (mod 256): same as x=0.
            Assert.Equal(Anim.WaveMult(0, 0, 0, 150, 255), Anim.WaveMult(16, 0, 0, 150, 255));
            // 8y with y=32 is 256 ≡ 0.
            Assert.Equal(Anim.WaveMult(0, 0, 0, 220, 255), Anim.WaveMult(0, 32, 0, 220, 255));
        }

        [Fact]
        public void FramePhaseIsWrappingU32()
        {
            // frame * 4 wraps mod 2^32: 0x4000_0000 * 4 == 0.
            Assert.Equal(
                Anim.WaveMult(3, 5, 0, 150, 255),
                Anim.WaveMult(3, 5, 0x40000000u, 150, 255));
        }

        [Fact]
        public void SpansTheFullLoHiRange()
        {
            // s = 255 (LUT peak) yields exactly hi; s = 1 (trough) yields lo
            // for all used spans. Index 64 needs phase 64: x=4, y=0, frame=0.
            Assert.Equal((byte)255, Anim.WaveMult(4, 0, 0, 150, 255));
            // Phase 192: x=12, y=0 → 192 → SIN=1 → 150 + 1*105/255 = 150.
            Assert.Equal((byte)150, Anim.WaveMult(12, 0, 0, 150, 255));
            Assert.Equal((byte)180, Anim.WaveMult(12, 0, 0, 180, 255));
            Assert.Equal((byte)220, Anim.WaveMult(12, 0, 0, 220, 255));
        }
    }

    public class ScaleRgbTests
    {
        [Fact]
        public void FullMultiplierIsIdentity()
        {
            Assert.Equal(new Rgb(255, 220, 0), Anim.ScaleRgb(new Rgb(255, 220, 0), 255));
            Assert.Equal(new Rgb(1, 128, 254), Anim.ScaleRgb(new Rgb(1, 128, 254), 255));
        }

        [Fact]
        public void ZeroMultiplierIsBlack()
        {
            // m = 0 → (c * 1) >> 8 == 0 for every 8-bit channel.
            Assert.Equal(new Rgb(0, 0, 0), Anim.ScaleRgb(new Rgb(255, 220, 0), 0));
        }

        [Fact]
        public void MidMultiplierTruncates()
        {
            // (255 * 128) >> 8 = 127, (220 * 128) >> 8 = 110.
            Assert.Equal(new Rgb(127, 110, 0), Anim.ScaleRgb(new Rgb(255, 220, 0), 127));
        }
    }

    public class FloorMathTests
    {
        // C# '/' and '%' truncate toward zero; these helpers floor instead.
        [Theory]
        [InlineData(5, 32, 5)]
        [InlineData(0, 32, 0)]
        [InlineData(-5, 32, 27)] // C# -5 % 32 == -5
        [InlineData(-32, 32, 0)]
        [InlineData(-33, 32, 31)]
        [InlineData(31, 32, 31)]
        [InlineData(-1, 4, 3)]
        public void FloorModMatchesRemEuclid(int a, int b, int expected)
        {
            Assert.Equal(expected, Anim.FloorMod(a, b));
        }

        [Theory]
        [InlineData(7, 4, 1)]
        [InlineData(4, 4, 1)]
        [InlineData(0, 4, 0)]
        [InlineData(-1, 4, -1)] // C# -1 / 4 == 0
        [InlineData(-4, 4, -1)]
        [InlineData(-5, 4, -2)]
        public void FloorDivMatchesDivEuclid(int a, int b, int expected)
        {
            Assert.Equal(expected, Anim.FloorDiv(a, b));
        }
    }

    public class FrameBufferTests
    {
        [Fact]
        public void OutOfRangeWritesAreSilentlyIgnored()
        {
            // The green onset sweep starts at x = -4 and relies on the paint
            // target clamping.
            var frameBuffer = new FrameBuffer();
            frameBuffer.SetPixel(-4, 0, 1, 2, 3);
            frameBuffer.SetPixel(0, -1, 1, 2, 3);
            frameBuffer.SetPixel(32, 0, 1, 2, 3);
            frameBuffer.SetPixel(0, 32, 1, 2, 3);
            foreach (byte b in frameBuffer.Pixels)
            {
                Assert.Equal(0, b);
            }
        }

        [Fact]
        public void PixelLayoutIsRowMajorRgb()
        {
            // Pixel (x, y) lives at byte offset (y * 32 + x) * 3, order R,G,B.
            var frameBuffer = new FrameBuffer();
            frameBuffer.SetPixel(3, 2, 10, 20, 30);
            int offset = (2 * 32 + 3) * 3;
            Assert.Equal(10, frameBuffer.Pixels[offset]);
            Assert.Equal(20, frameBuffer.Pixels[offset + 1]);
            Assert.Equal(30, frameBuffer.Pixels[offset + 2]);
            Assert.Equal(new Rgb(10, 20, 30), frameBuffer.GetPixel(3, 2));
            Assert.Equal(new Rgb(0, 0, 0), frameBuffer.GetPixel(-1, 2));
        }
    }
}
