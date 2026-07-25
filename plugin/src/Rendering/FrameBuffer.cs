// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// 32×32 RGB888 paint target. Byte layout matches the Frame packet's payload
// exactly (docs/protocol.md): row-major, pixel (x, y) at byte offset
// (y * 32 + x) * 3, channel order R, G, B — 3072 bytes total, so a finished
// buffer goes on the wire without a repack.

namespace Uniflag.Rendering
{
    /// <summary>
    /// In-memory RGB888 frame buffer. Coordinates are signed and
    /// out-of-range writes are <b>silently ignored</b> — effect arithmetic
    /// genuinely goes negative (the green onset sweep band centre starts at
    /// x = -4), so this clamp is part of the rendering contract, not a
    /// convenience.
    /// </summary>
    public sealed class FrameBuffer
    {
        public const int Width = 32;
        public const int Height = 32;

        public const int ByteLength = Width * Height * 3;

        private readonly byte[] _pixels = new byte[ByteLength];

        /// <summary>
        /// Backing store in golden-frame layout. Exposed directly for
        /// zero-copy streaming; treat as read-only outside the renderer.
        /// </summary>
        public byte[] Pixels => _pixels;

        /// <summary>Write one pixel; out-of-range coordinates are ignored.</summary>
        public void SetPixel(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return;
            }
            int i = (y * Width + x) * 3;
            _pixels[i] = r;
            _pixels[i + 1] = g;
            _pixels[i + 2] = b;
        }

        /// <summary>Write one pixel; out-of-range coordinates are ignored.</summary>
        public void SetPixel(int x, int y, Rgb color) => SetPixel(x, y, color.R, color.G, color.B);

        /// <summary>
        /// Read one pixel. Out-of-range coordinates return black, matching
        /// the silently-ignored writes.
        /// </summary>
        public Rgb GetPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return new Rgb(0, 0, 0);
            }
            int i = (y * Width + x) * 3;
            return new Rgb(_pixels[i], _pixels[i + 1], _pixels[i + 2]);
        }
    }
}
