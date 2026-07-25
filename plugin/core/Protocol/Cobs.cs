// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Protocol
{
    /// <summary>
    /// COBS (Consistent Overhead Byte Stuffing) framing — port of
    /// <c>proto/src/cobs.rs</c>.
    ///
    /// Encoded output never contains <c>0x00</c>; packets are delimited on
    /// the wire by a single <c>0x00</c> <b>which these functions neither
    /// produce nor consume</b> — the transport layer appends it after
    /// <see cref="Encode(byte[])"/> and strips it before
    /// <see cref="Decode(byte[])"/>. That property is what makes stream
    /// resync trivial: after any corruption, skip to the next <c>0x00</c>
    /// and the decoder is realigned.
    ///
    /// Canonical form: this encoder always terminates with a group header —
    /// Cheshire &amp; Baker's Listing 1 (<c>StuffData</c>), the same
    /// convention as the Rust <c>proto::cobs</c> module — so a payload that
    /// is an exact multiple of 254 non-zero bytes ends with a trailing
    /// <c>0x01</c> code byte. <b>Beware: Wikipedia's <c>cobsEncode</c>
    /// example omits that trailing byte</b>, and the divergence is silent
    /// under round-trip testing because the decoder (like every decoder)
    /// accepts both forms. The cross-language golden vectors pin the
    /// Listing-1 choice and include a 254-boundary case precisely so a
    /// Wikipedia-derived port fails loudly.
    /// </summary>
    public static class Cobs
    {
        /// <summary>
        /// Worst-case encoded size for a <paramref name="payloadLength"/>-byte
        /// payload (excluding the wire delimiter): one code byte per started
        /// 254-byte group, plus the always-emitted final group header (an
        /// extra byte when the payload is an exact non-zero multiple of 254).
        /// Exactly tight for zero-free payloads.
        /// </summary>
        public static int MaxEncodedLength(int payloadLength)
        {
            if (payloadLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }
            return payloadLength + payloadLength / 254 + 1;
        }

        /// <summary>Encode <paramref name="count"/> bytes of <paramref name="src"/>; returns a newly allocated exact-size array.</summary>
        public static byte[] Encode(byte[] src, int offset, int count)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }
            if (offset < 0 || count < 0 || offset + count > src.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            // MaxEncodedLength over-estimates for input with zeros; trim after.
            var dst = new byte[MaxEncodedLength(count)];
            // codeIdx is the reserved slot for the current group's code byte;
            // outIdx is the next free slot.
            int codeIdx = 0;
            int outIdx = 1;
            byte code = 1;

            for (int i = offset; i < offset + count; i++)
            {
                byte b = src[i];
                if (b == 0)
                {
                    dst[codeIdx] = code;
                    codeIdx = outIdx;
                    outIdx++;
                    code = 1;
                }
                else
                {
                    dst[outIdx] = b;
                    outIdx++;
                    code++;
                    if (code == 0xFF)
                    {
                        dst[codeIdx] = code;
                        codeIdx = outIdx;
                        outIdx++;
                        code = 1;
                    }
                }
            }
            dst[codeIdx] = code;

            if (outIdx == dst.Length)
            {
                return dst;
            }
            var result = new byte[outIdx];
            Array.Copy(dst, result, outIdx);
            return result;
        }

        /// <summary>Encode a whole array.</summary>
        public static byte[] Encode(byte[] src)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }
            return Encode(src, 0, src.Length);
        }

        /// <summary>
        /// Decode <paramref name="count"/> bytes of <paramref name="src"/>
        /// (one delimiter-stripped encoded packet) into a newly allocated
        /// exact-size array. Strict: rejects embedded <c>0x00</c> anywhere
        /// (including group data), group overruns, and empty input with
        /// <see cref="ProtocolErrorKind.CobsMalformed"/>. Liberal only in
        /// accepting the non-canonical short form (a full final group
        /// without the trailing <c>0x01</c> header).
        /// </summary>
        public static byte[] Decode(byte[] src, int offset, int count)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }
            if (offset < 0 || count < 0 || offset + count > src.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (count == 0)
            {
                // The empty payload encodes to [0x01], never to nothing.
                throw new ProtocolException(ProtocolErrorKind.CobsMalformed, "empty COBS input");
            }

            // Decoded output is always shorter than the input: every group
            // consumes a code byte and yields at most (code - 1) data bytes
            // plus at most one implied zero.
            var dst = new byte[count - 1];
            int outIdx = 0;
            int i = 0;
            while (i < count)
            {
                byte code = src[offset + i];
                if (code == 0)
                {
                    throw new ProtocolException(
                        ProtocolErrorKind.CobsMalformed,
                        "0x00 code byte inside COBS data");
                }
                i++;
                int run = code - 1;
                if (i + run > count)
                {
                    throw new ProtocolException(
                        ProtocolErrorKind.CobsMalformed,
                        "COBS group runs past the end of the input");
                }
                for (int j = 0; j < run; j++)
                {
                    byte b = src[offset + i + j];
                    if (b == 0)
                    {
                        // A valid encoding never contains 0x00 anywhere —
                        // including group data. Unreachable from a
                        // delimiter-splitting transport, but the strictness
                        // keeps decoder behaviour fully defined for the
                        // cross-language conformance vectors.
                        throw new ProtocolException(
                            ProtocolErrorKind.CobsMalformed,
                            "0x00 inside COBS group data");
                    }
                    dst[outIdx] = b;
                    outIdx++;
                }
                i += run;
                // Every group except a full (0xFF) one and the final one
                // stands in for a zero byte of the payload.
                if (code != 0xFF && i < count)
                {
                    dst[outIdx] = 0;
                    outIdx++;
                }
            }

            if (outIdx == dst.Length)
            {
                return dst;
            }
            var result = new byte[outIdx];
            Array.Copy(dst, result, outIdx);
            return result;
        }

        /// <summary>Decode a whole array.</summary>
        public static byte[] Decode(byte[] src)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }
            return Decode(src, 0, src.Length);
        }
    }
}
