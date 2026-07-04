// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Streaming decoder tests — the C# analogue of cli/src/rx.rs's decoder
// suite, pinning the receiver posture of docs/protocol.md §Framing:
// resync at 0x00, silent drops for bad segments, no-op empty spans,
// oversized accumulations dropped until realignment, and unknown types
// surfaced (not errored) for the caller to skip.

using System.Collections.Generic;
using Uniflag.Device;
using Uniflag.Protocol;
using Xunit;

namespace Uniflag.Tests
{
    public class PacketStreamDecoderTests
    {
        [Fact]
        public void SplitFeedsReassembleAcrossChunkBoundaries()
        {
            byte[] wire = new BrightnessPacket(200).EncodeWire();

            var whole = new PacketStreamDecoder();
            List<Packet> expected = whole.Feed(wire, 0, wire.Length);
            Assert.Single(expected);
            Assert.Equal(new BrightnessPacket(200), expected[0]);

            // Byte-at-a-time produces the identical packet sequence.
            var trickle = new PacketStreamDecoder();
            var seen = new List<Packet>();
            for (int i = 0; i < wire.Length; i++)
            {
                seen.AddRange(trickle.Feed(wire, i, 1));
            }
            Assert.Equal(expected, seen);
        }

        [Fact]
        public void BackToBackDelimitersAreNoOps()
        {
            var decoder = new PacketStreamDecoder();
            Assert.Empty(decoder.Feed(new byte[] { 0x00, 0x00, 0x00 }, 0, 3));
            Assert.Equal(0, decoder.DroppedSegments);
        }

        [Fact]
        public void OversizedAccumulationDropsUntilTheNextDelimiter()
        {
            var decoder = new PacketStreamDecoder();
            // More delimiter-less non-zero bytes than any legal packet.
            var garbage = new byte[PacketCodec.MaxWireLength + 512];
            for (int i = 0; i < garbage.Length; i++)
            {
                garbage[i] = 0x42;
            }
            Assert.Empty(decoder.Feed(garbage, 0, garbage.Length));

            // The delimiter flushes the drop; the next valid packet decodes
            // cleanly — the decoder realigned.
            byte[] hello = new HelloPacket(PacketCodec.ProtocolVersion).EncodeWire();
            var tail = new byte[1 + hello.Length];
            tail[0] = 0x00;
            System.Array.Copy(hello, 0, tail, 1, hello.Length);
            List<Packet> packets = decoder.Feed(tail, 0, tail.Length);
            Assert.Single(packets);
            Assert.Equal(new HelloPacket(PacketCodec.ProtocolVersion), packets[0]);
            Assert.Equal(1, decoder.DroppedSegments);
        }

        [Fact]
        public void CorruptedSegmentIsDroppedAndTheStreamResyncs()
        {
            byte[] good = new BrightnessPacket(200).EncodeWire();
            byte[] corrupted = new BrightnessPacket(200).EncodeWire();
            // Overwrite one COBS body byte with a different non-zero value
            // (0x00 would split the segment instead of corrupting it).
            corrupted[1] = corrupted[1] == 0x55 ? (byte)0x66 : (byte)0x55;

            var decoder = new PacketStreamDecoder();
            var seen = new List<Packet>();
            seen.AddRange(decoder.Feed(corrupted, 0, corrupted.Length));
            seen.AddRange(decoder.Feed(good, 0, good.Length));
            Assert.Single(seen); // the corrupted segment vanished silently
            Assert.Equal(new BrightnessPacket(200), seen[0]);
            Assert.Equal(1, decoder.DroppedSegments);
        }

        [Fact]
        public void UnknownTypeSurfacesAsUnknownPacketNotAnError()
        {
            byte[] wire = PacketCodec.EncodeWire(0x7E, new byte[] { 0x42 });
            var decoder = new PacketStreamDecoder();
            List<Packet> packets = decoder.Feed(wire, 0, wire.Length);
            Assert.Single(packets);
            var unknown = Assert.IsType<UnknownPacket>(packets[0]);
            Assert.Equal(0x7E, unknown.TypeByte);
            Assert.Equal(0, decoder.DroppedSegments); // ignored, never dropped
        }

        [Fact]
        public void HelloAckAndButtonEventInOneChunkBothDecode()
        {
            // The device→host case the RX pump actually sees: an ack with a
            // ButtonEvent hard against it in the same USB transfer.
            byte[] ack = DeviceTestUtil.HelloAckWire();
            byte[] button = DeviceTestUtil.ButtonEventWire(0, 0);
            var chunk = new byte[ack.Length + button.Length];
            System.Array.Copy(ack, chunk, ack.Length);
            System.Array.Copy(button, 0, chunk, ack.Length, button.Length);

            var decoder = new PacketStreamDecoder();
            List<Packet> packets = decoder.Feed(chunk, 0, chunk.Length);
            Assert.Equal(2, packets.Count);
            Assert.IsType<HelloAckPacket>(packets[0]);
            Assert.IsType<ButtonEventPacket>(packets[1]);
        }
    }
}
