// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Protocol
{
    /// <summary>
    /// Cross-language error classes of the v2 wire protocol, mirroring the
    /// Rust <c>cobs::Error</c> / <c>packet::Error</c> split. The first three
    /// are the contract classes named by <c>testdata/proto/README.md</c>
    /// (<c>cobs_malformed</c>, <c>bad_crc</c>, <c>bad_length</c>); receivers
    /// drop the offending packet and resync at the next <c>0x00</c>.
    /// </summary>
    public enum ProtocolErrorKind
    {
        /// <summary>
        /// COBS input malformed: embedded <c>0x00</c> (anywhere, including
        /// group data), a group running past the end of the input, or an
        /// empty input. Rust: <c>cobs::Error::Malformed</c>.
        /// </summary>
        CobsMalformed,

        /// <summary>CRC mismatch — drop the packet and resync. Rust: <c>packet::Error::BadCrc</c>.</summary>
        BadCrc,

        /// <summary>
        /// Known packet type whose payload length doesn't match its declared
        /// layout (or, encode-side, an over-long HelloAck <c>fw_version</c>).
        /// Rust: <c>packet::Error::BadLength</c>.
        /// </summary>
        BadLength,

        /// <summary>Raw packet shorter than type + CRC. Rust: <c>packet::Error::TooShort</c>.</summary>
        TooShort,
    }

    /// <summary>
    /// Thrown by the codec on malformed input. <see cref="Kind"/> carries the
    /// cross-language error class the conformance vectors are defined against.
    /// </summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolErrorKind Kind { get; }

        public ProtocolException(ProtocolErrorKind kind, string message)
            : base(message)
        {
            Kind = kind;
        }
    }
}
