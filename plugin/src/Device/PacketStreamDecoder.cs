// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Inbound decode pipeline: a 0x00-delimited COBS byte stream in, typed
// packets out — the C# analogue of cli/src/rx.rs::Decoder, built on the
// frozen Uniflag.Protocol codec. Mirrors the receiver posture of
// docs/protocol.md §Framing: malformed COBS, bad CRCs, wrong-length known
// types, and oversized accumulations are dropped silently and the decoder
// realigns at the next delimiter; back-to-back delimiters are no-ops;
// CRC-valid packets with unassigned type bytes surface as UnknownPacket so
// callers can ignore them explicitly (forward compat).

using System.Collections.Generic;
using Uniflag.Protocol;

namespace Uniflag.Device
{
    /// <summary>
    /// Streaming packet decoder: accumulates bytes, splits on <c>0x00</c>
    /// delimiters, and runs each complete segment through COBS decode → CRC
    /// check → typed parse. Feed it whatever chunk sizes the transport
    /// hands you; partial segments stay buffered between feeds. Not
    /// thread-safe — one decoder per connection, fed from one thread.
    /// </summary>
    public sealed class PacketStreamDecoder
    {
        // Fixed accumulator: a delimiter-stripped segment can be at most
        // MaxWireLength - 1 bytes (the delimiter itself is consumed by the
        // split); anything that reaches MaxWireLength without a delimiter
        // is longer than any legal packet and is dropped until realignment.
        private readonly byte[] _pending = new byte[PacketCodec.MaxWireLength];
        private int _pendingLength;
        private bool _overflowed;
        private long _droppedSegments;

        /// <summary>
        /// Segments dropped so far (bad COBS/CRC/length, or oversized
        /// accumulations). Diagnostic only — drops are silent by contract.
        /// </summary>
        public long DroppedSegments => _droppedSegments;

        /// <summary>
        /// Consume <paramref name="count"/> bytes, returning one typed
        /// packet per completed valid segment, in stream order.
        /// </summary>
        public List<Packet> Feed(byte[] data, int offset, int count)
        {
            var packets = new List<Packet>();
            for (int i = offset; i < offset + count; i++)
            {
                byte b = data[i];
                if (b == PacketCodec.Delimiter)
                {
                    if (_overflowed)
                    {
                        _droppedSegments++;
                    }
                    else if (_pendingLength > 0)
                    {
                        DecodeSegment(packets);
                    }
                    // Empty delimiter-to-delimiter spans are no-ops.
                    _pendingLength = 0;
                    _overflowed = false;
                }
                else if (!_overflowed)
                {
                    _pending[_pendingLength++] = b;
                    if (_pendingLength >= PacketCodec.MaxWireLength)
                    {
                        // Longer than any legal packet: drop until the next
                        // delimiter realigns the stream.
                        _pendingLength = 0;
                        _overflowed = true;
                    }
                }
            }
            return packets;
        }

        private void DecodeSegment(List<Packet> packets)
        {
            try
            {
                // UnknownPacket comes back as a packet, not an exception —
                // ignoring it is the caller's job (forward compat).
                packets.Add(PacketCodec.DecodeWire(_pending, 0, _pendingLength));
            }
            catch (ProtocolException)
            {
                // Malformed COBS / bad CRC / wrong-length known type: drop
                // the segment silently, same posture as the firmware.
                _droppedSegments++;
            }
        }
    }
}
