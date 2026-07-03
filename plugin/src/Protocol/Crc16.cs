// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Protocol
{
    /// <summary>
    /// CRC-16 for the v2 binary protocol — byte-for-byte port of
    /// <c>proto/src/crc.rs</c>.
    ///
    /// Parameters — <b>CRC-16/CCITT-FALSE</b>: poly <c>0x1021</c>, init
    /// <c>0xFFFF</c>, no reflection (refin/refout false), xorout <c>0</c>,
    /// check value <c>crc("123456789") == 0x29B1</c>.
    ///
    /// The CRC is computed over the <i>raw</i> packet bytes (type byte +
    /// payload), before COBS encoding. See <see cref="PacketCodec"/> for the
    /// framing layout.
    /// </summary>
    public static class Crc16
    {
        private const ushort Poly = 0x1021;

        /// <summary>Initial CRC register value. Feed to <see cref="Update(ushort, byte[], int, int)"/> when streaming.</summary>
        public const ushort Init = 0xFFFF;

        private static readonly ushort[] Table = BuildTable();

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)(i << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ Poly)
                        : (ushort)(crc << 1);
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>Streaming update: fold <paramref name="count"/> bytes of <paramref name="data"/> into a running CRC started from <see cref="Init"/>.</summary>
        public static ushort Update(ushort crc, byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            for (int i = offset; i < offset + count; i++)
            {
                int idx = ((crc >> 8) ^ data[i]) & 0xFF;
                crc = (ushort)((crc << 8) ^ Table[idx]);
            }
            return crc;
        }

        /// <summary>Streaming update over a whole array.</summary>
        public static ushort Update(ushort crc, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return Update(crc, data, 0, data.Length);
        }

        /// <summary>One-shot CRC of <paramref name="count"/> bytes of <paramref name="data"/>.</summary>
        public static ushort Checksum(byte[] data, int offset, int count)
        {
            return Update(Init, data, offset, count);
        }

        /// <summary>One-shot CRC of <paramref name="data"/>.</summary>
        public static ushort Checksum(byte[] data)
        {
            return Update(Init, data);
        }
    }
}
