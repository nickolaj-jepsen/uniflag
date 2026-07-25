// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Minimal PNG writer. Hand-rolled because the alternatives don't fit:
// System.Drawing is Windows-only on modern .NET, and pulling an imaging
// package into a dev tool to write 32×32 truecolour images is not a trade
// worth making. Truecolour, 8-bit, no interlacing, filter type 0 — the
// simplest thing the spec allows.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Uniflag.Tools
{
    internal static class Png
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>
        /// Encode RGB888 bytes (row-major, 3 bytes per pixel) as a PNG,
        /// nearest-neighbour upscaled by <paramref name="scale"/>. Upscaling
        /// matters: a 32×32 image is unreadable at native size in every
        /// viewer, and nearest-neighbour keeps the pixel grid honest where
        /// smooth scaling would invent colours that were never rendered.
        /// </summary>
        public static byte[] Encode(byte[] rgb, int width, int height, int scale)
        {
            if (rgb == null)
            {
                throw new ArgumentNullException(nameof(rgb));
            }
            if (rgb.Length != width * height * 3)
            {
                throw new ArgumentException(
                    $"expected {width * height * 3} bytes for {width}×{height}, got {rgb.Length}",
                    nameof(rgb));
            }
            if (scale < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be >= 1");
            }

            int outW = width * scale;
            int outH = height * scale;

            // Raw PNG image data: each scanline prefixed with its filter byte.
            var raw = new byte[outH * (1 + outW * 3)];
            int p = 0;
            for (int y = 0; y < outH; y++)
            {
                raw[p++] = 0; // filter: none
                int srcRow = (y / scale) * width * 3;
                for (int x = 0; x < outW; x++)
                {
                    int src = srcRow + (x / scale) * 3;
                    raw[p++] = rgb[src];
                    raw[p++] = rgb[src + 1];
                    raw[p++] = rgb[src + 2];
                }
            }

            using var deflated = new MemoryStream();
            // ZLibStream (not DeflateStream) — PNG's IDAT is a zlib stream,
            // header and Adler-32 included.
            using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw, 0, raw.Length);
            }

            using var png = new MemoryStream();
            png.Write(Signature, 0, Signature.Length);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)outW);
            WriteBigEndian(ihdr, 4, (uint)outH);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // colour type: truecolour
            ihdr[10] = 0; // compression: deflate
            ihdr[11] = 0; // filter method: adaptive
            ihdr[12] = 0; // interlace: none
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", deflated.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());

            return png.ToArray();
        }

        private static void WriteChunk(Stream to, string type, byte[] data)
        {
            var header = new byte[4];
            WriteBigEndian(header, 0, (uint)data.Length);
            to.Write(header, 0, 4);

            var typed = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++)
            {
                typed[i] = (byte)type[i];
            }
            Array.Copy(data, 0, typed, 4, data.Length);
            to.Write(typed, 0, typed.Length);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, Crc32(typed));
            to.Write(crc, 0, 4);
        }

        private static void WriteBigEndian(byte[] into, int at, uint value)
        {
            into[at] = (byte)(value >> 24);
            into[at + 1] = (byte)(value >> 16);
            into[at + 2] = (byte)(value >> 8);
            into[at + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(IReadOnlyList<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Count; i++)
            {
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }
    }
}
