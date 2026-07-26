// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Terminal rendering of a frame, for when you want to see a signal without
// leaving the shell — and so a render can be *shown* in CI logs and tool
// output rather than described.
//
// One character per two pixel rows: the upper half-block glyph gets the top
// pixel as its foreground and the bottom pixel as its background, so a 32x32
// frame lands in 16 terminal lines at true aspect ratio.

using System.Text;

namespace Uniflag.Tools
{
    internal static class Ansi
    {
        /// <summary>U+2580 UPPER HALF BLOCK.</summary>
        private const string UpperHalfBlock = "▀";

        // Built from the code point rather than written as an escape: a raw
        // ESC byte in source is invisible and travels badly through tooling.
        private static readonly string Esc = ((char)0x1B).ToString();
        private static readonly string Reset = Esc + "[0m";

        public static string Render(byte[] rgb, int width, int height)
        {
            var sb = new StringBuilder();
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x++)
                {
                    (byte tr, byte tg, byte tb) = At(rgb, width, x, y);
                    (byte br, byte bg, byte bb) = y + 1 < height
                        ? At(rgb, width, x, y + 1)
                        : ((byte)0, (byte)0, (byte)0);

                    sb.Append(Esc).Append("[38;2;")
                      .Append(tr).Append(';').Append(tg).Append(';').Append(tb).Append('m');
                    sb.Append(Esc).Append("[48;2;")
                      .Append(br).Append(';').Append(bg).Append(';').Append(bb).Append('m');
                    sb.Append(UpperHalfBlock);
                }
                sb.Append(Reset).Append('\n');
            }
            return sb.ToString();
        }

        private static (byte, byte, byte) At(byte[] rgb, int width, int x, int y)
        {
            int i = (y * width + x) * 3;
            return (rgb[i], rgb[i + 1], rgb[i + 2]);
        }
    }
}
