// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Conversation tests for the Hello/HelloAck handshake against the
// in-memory duplex fake — no real serial ports. Pins the host obligations
// of docs/protocol.md §Handshake: byte-exact Hello, protocol-version
// EQUALITY validated by the host (refuse-with-message), 32x32 panel
// validation, and the skip-silently rule for device→host packets arriving
// before the ack (a ButtonEvent can wedge across sessions).

using Uniflag.Device;
using Uniflag.Protocol;
using Xunit;

namespace Uniflag.Tests
{
    public class DeviceHandshakeTests
    {
        private const int TimeoutMs = 2000;

        private static HandshakeResult Perform(FakeConnection connection, int timeoutMs = TimeoutMs)
        {
            return DeviceHandshake.Perform(connection, timeoutMs, () => false);
        }

        [Fact]
        public void SendsExactlyOneByteExactHello()
        {
            var connection = new FakeConnection();
            connection.EnqueueRx(DeviceTestUtil.HelloAckWire());
            Perform(connection);
            // The codec's wire bytes are themselves pinned by the M6 golden
            // vectors (ProtoConformanceTests), so equality here is
            // byte-exactness against the committed protocol bytes.
            Assert.Equal(
                new HelloPacket(PacketCodec.ProtocolVersion).EncodeWire(),
                connection.TxBytes);
        }

        [Fact]
        public void SucceedsOnAValidAckAndRecordsTheDeviceIdentity()
        {
            var connection = new FakeConnection();
            connection.EnqueueRx(DeviceTestUtil.HelloAckWire(fwVersion: "0.2.0"));
            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Null(result.Message);
            Assert.Equal(PacketCodec.ProtocolVersion, result.Ack.ProtocolVersion);
            Assert.Equal(32, result.Ack.Width);
            Assert.Equal(32, result.Ack.Height);
            Assert.Equal("0.2.0", result.Ack.FwVersionString);
            Assert.NotNull(result.Decoder);
            Assert.Empty(result.TrailingPackets);
        }

        [Fact]
        public void SkipsDeviceToHostPacketsArrivingBeforeTheAck()
        {
            // A ButtonEvent wedged in the device's in-flight write from the
            // previous session lands ahead of the ack — the host must skip
            // it silently, not error and not act on it.
            var connection = new FakeConnection();
            connection.EnqueueRx(DeviceTestUtil.ButtonEventWire(0, 0));
            connection.EnqueueRx(DeviceTestUtil.ButtonEventWire(2, 1));
            connection.EnqueueRx(DeviceTestUtil.HelloAckWire());
            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Empty(result.TrailingPackets); // pre-ack events are gone, not deferred
        }

        [Fact]
        public void RefusesOnProtocolVersionMismatch()
        {
            var connection = new FakeConnection();
            connection.EnqueueRx(DeviceTestUtil.HelloAckWire(protocolVersion: 2));
            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Refused, result.Outcome);
            Assert.Contains("mismatch", result.Message);
            Assert.Contains("v" + PacketCodec.ProtocolVersion, result.Message);
            Assert.Contains("v2", result.Message);
            Assert.Null(result.Ack); // refused means never driven
        }

        [Fact]
        public void RefusesOnPanelSizeMismatch()
        {
            var connection = new FakeConnection();
            connection.EnqueueRx(DeviceTestUtil.HelloAckWire(width: 16, height: 16));
            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Refused, result.Outcome);
            Assert.Contains("16x16", result.Message);
            Assert.Null(result.Ack);
        }

        [Fact]
        public void TimesOutWhenNothingAnswers()
        {
            var connection = new FakeConnection();
            HandshakeResult result = Perform(connection, timeoutMs: 100);
            Assert.Equal(HandshakeOutcome.NoAck, result.Outcome);
            Assert.Contains("No HelloAck", result.Message);
        }

        [Fact]
        public void ReassemblesAnAckTrickledOneByteAtATime()
        {
            var connection = new FakeConnection();
            byte[] ack = DeviceTestUtil.HelloAckWire();
            for (int i = 0; i < ack.Length; i++)
            {
                connection.EnqueueRx(new[] { ack[i] });
            }
            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        }

        [Fact]
        public void HandsPacketsBehindTheAckToTheCallerUnlost()
        {
            // A post-ack ButtonEvent in the same read chunk must survive the
            // handshake/streaming handover.
            var connection = new FakeConnection();
            byte[] ack = DeviceTestUtil.HelloAckWire();
            byte[] button = DeviceTestUtil.ButtonEventWire(0, 0);
            var chunk = new byte[ack.Length + button.Length];
            System.Array.Copy(ack, chunk, ack.Length);
            System.Array.Copy(button, 0, chunk, ack.Length, button.Length);
            connection.EnqueueRx(chunk);

            HandshakeResult result = Perform(connection);
            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Single(result.TrailingPackets);
            Assert.IsType<ButtonEventPacket>(result.TrailingPackets[0]);
        }
    }
}
