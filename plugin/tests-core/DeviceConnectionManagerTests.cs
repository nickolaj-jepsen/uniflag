// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Connection manager lifecycle tests against injected fakes — no real
// serial ports, CI-safe. Covers: the scan → open (retry-on-open) →
// handshake → Brightness → streaming walk; the Brightness re-send on every
// (re)connect; pre-ack ButtonEvent isolation; refuse-with-message on
// version mismatch; ButtonEvent → policy → Brightness packet; sink
// registration bounded to the streaming state; 30 fps even-tick decimation;
// newest-frame coalescing under a stalled writer; manual override; and
// teardown (Stop must release the port and be idempotent — SimHub re-Inits
// at every game change).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Uniflag.Device;
using Uniflag.Protocol;
using Xunit;
using static Uniflag.Tests.DeviceTestUtil;

namespace Uniflag.Tests
{
    public class DeviceConnectionManagerTests
    {
        private const string DevicePort = "COM7";

        /// <summary>
        /// One manager with all seams faked. The enumerator advertises a
        /// uniflag device on COM7 by default; connections must be scripted
        /// per test.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            internal readonly FakePortEnumerator Enumerator = new FakePortEnumerator();
            internal readonly FakeConnectionFactory Factory = new FakeConnectionFactory();
            internal readonly CountingSinkHost Host = new CountingSinkHost();
            internal readonly BrightnessPolicy Policy = new BrightnessPolicy(80);
            internal readonly DeviceConnectionManager Manager;

            internal Harness(DeviceConnectionOptions options = null)
            {
                Enumerator.SetPorts(new SerialPortInfo(
                    DevicePort, DeviceDiscovery.UsbVendorId, DeviceDiscovery.UsbProductIdTest));
                Manager = new DeviceConnectionManager(
                    Host, Enumerator, Factory, Policy, options ?? FastOptions());
            }

            public void Dispose()
            {
                Manager.Dispose();
            }
        }

        /// <summary>Shrunk timings so lifecycle transitions complete in milliseconds.</summary>
        private static DeviceConnectionOptions FastOptions()
        {
            return new DeviceConnectionOptions
            {
                ScanIntervalMs = 10,
                OpenRetryBackoffMs = new[] { 5, 5 },
                HandshakeTimeoutMs = 2000,
                RefusedRetryMs = 20,
                TxIdlePollMs = 10,
                MinBrightnessIntervalMs = 0,
            };
        }

        private static FakeConnection AnsweringConnection()
        {
            var connection = new FakeConnection(DevicePort);
            connection.EnqueueRx(HelloAckWire());
            return connection;
        }

        private static byte[] PatternFrame(byte seed)
        {
            var frame = new byte[PacketCodec.FramePayloadLength];
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)(i + seed);
            }
            return frame;
        }

        private static List<Packet> TxPackets(FakeConnection connection)
        {
            byte[] bytes = connection.TxBytes;
            return DecodeStream(bytes);
        }

        [Fact]
        public void ConnectsWithHelloThenBrightnessThenFrames()
        {
            using (var h = new Harness())
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "the streaming state");
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is BrightnessPacket),
                    "the (re)connect Brightness send");

                h.Manager.OnFrame(PatternFrame(1), 0);
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is FramePacket),
                    "the first frame on the wire");

                // Wire order is the protocol's handshake order: Hello, then
                // Brightness (the persisted value), then Frames.
                List<Packet> packets = TxPackets(connection);
                Assert.Equal(new HelloPacket(PacketCodec.ProtocolVersion), packets[0]);
                Assert.Equal(new BrightnessPacket(80), packets[1]);
                Assert.IsType<FramePacket>(packets[2]);

                DeviceStatus status = h.Manager.Status;
                Assert.Equal(DevicePort, status.PortName);
                Assert.Equal("0.2.0", status.FirmwareVersion);
                Assert.Equal(PacketCodec.ProtocolVersion, status.ProtocolVersion);
                Assert.Equal((byte)32, status.PanelWidth);
                Assert.Equal((byte)32, status.PanelHeight);
            }
        }

        [Fact]
        public void OddTicksAreDecimatedAwayEvenTicksFlow()
        {
            using (var h = new Harness())
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "the streaming state");

                // Odd tick indices: 60 fps renderer clock decimated to 30.
                h.Manager.OnFrame(PatternFrame(1), 1);
                h.Manager.OnFrame(PatternFrame(2), 3);
                Thread.Sleep(100); // several TX pump cycles at TxIdlePollMs=10
                Assert.DoesNotContain(TxPackets(connection), p => p is FramePacket);

                h.Manager.OnFrame(PatternFrame(3), 4);
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is FramePacket),
                    "the even-tick frame");
            }
        }

        [Fact]
        public void PreAckButtonEventNeverReachesThePolicy()
        {
            using (var h = new Harness())
            {
                var connection = new FakeConnection(DevicePort);
                // The wedged-across-sessions packet: a brightness-up press
                // delivered ahead of the ack.
                connection.EnqueueRx(ButtonEventWire(0, 0));
                connection.EnqueueRx(HelloAckWire());
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "the streaming state");
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is BrightnessPacket),
                    "the (re)connect Brightness send");

                // Had the pre-ack press leaked, the policy would sit at 92
                // and the connect-time Brightness would carry 92.
                Assert.Equal(80, h.Policy.Current);
                List<byte> brightnessValues = TxPackets(connection)
                    .OfType<BrightnessPacket>()
                    .Select(p => p.Value)
                    .ToList();
                Assert.Equal(new List<byte> { 80 }, brightnessValues);
            }
        }

        [Fact]
        public void VersionMismatchIsRefusedWithAMessageAndNeverDriven()
        {
            using (var h = new Harness())
            {
                var connection = new FakeConnection(DevicePort);
                connection.EnqueueRx(HelloAckWire(protocolVersion: 2));
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                // Capture the snapshot inside the wait: with the shrunk
                // RefusedRetryMs the manager soon retries and the live
                // status flips back to Connecting/Scanning.
                DeviceStatus status = null;
                WaitUntil(
                    () =>
                    {
                        DeviceStatus s = h.Manager.Status;
                        if (s.State == DeviceConnectionState.Refused)
                        {
                            status = s;
                            return true;
                        }
                        return false;
                    },
                    "the refused state");

                Assert.Contains("mismatch", status.LastError);
                Assert.Contains("mismatch", status.StatusText); // surfaced verbatim in the tab
                Assert.Equal(DevicePort, status.PortName);

                // Refused means refused: no sink registration, no frames, no
                // Brightness — the Hello is the only thing on the wire.
                Assert.Equal(0, h.Host.Adds);
                List<Packet> packets = TxPackets(connection);
                Assert.All(packets, p => Assert.IsType<HelloPacket>(p));
            }
        }

        [Fact]
        public void PanelSizeMismatchIsRefusedWithAMessage()
        {
            using (var h = new Harness())
            {
                var connection = new FakeConnection(DevicePort);
                connection.EnqueueRx(HelloAckWire(width: 16, height: 16));
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                DeviceStatus status = null;
                WaitUntil(
                    () =>
                    {
                        DeviceStatus s = h.Manager.Status;
                        if (s.State == DeviceConnectionState.Refused)
                        {
                            status = s;
                            return true;
                        }
                        return false;
                    },
                    "the refused state");
                Assert.Contains("16x16", status.LastError);
                Assert.Equal(0, h.Host.Adds);
            }
        }

        [Fact]
        public void BrightnessIsResentOnEveryReconnect()
        {
            using (var h = new Harness())
            {
                FakeConnection first = AnsweringConnection();
                FakeConnection second = AnsweringConnection();
                h.Factory.EnqueueConnection(first);
                h.Factory.EnqueueConnection(second);
                h.Manager.Start();
                WaitUntil(
                    () => TxPackets(first).Any(p => p is BrightnessPacket),
                    "the first connect's Brightness");

                first.FailReads(); // yank: the RX pump faults
                WaitUntil(
                    () => TxPackets(second).Any(p => p is BrightnessPacket),
                    "the reconnect's Brightness");

                // Full handshake sequence again on the new session — the
                // device never assumes a value survives a reconnect.
                List<Packet> packets = TxPackets(second);
                Assert.Equal(new HelloPacket(PacketCodec.ProtocolVersion), packets[0]);
                Assert.Equal(new BrightnessPacket(80), packets[1]);
                Assert.True(first.Disposed, "the dead connection must be closed");
            }
        }

        [Fact]
        public void ButtonEventStepsThePolicyAndEmitsOneBrightnessPacket()
        {
            using (var h = new Harness())
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is BrightnessPacket),
                    "the (re)connect Brightness send");

                connection.EnqueueRx(ButtonEventWire(0, 0)); // brightness-up, short
                WaitUntil(() => h.Policy.Current == 92, "the policy step");
                WaitUntil(
                    () => TxPackets(connection).OfType<BrightnessPacket>().Any(p => p.Value == 92),
                    "the stepped Brightness on the wire");
            }
        }

        [Fact]
        public void SinkIsRegisteredWhileStreamingAndRemovedOnDisconnect()
        {
            using (var h = new Harness())
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(() => h.Host.Adds == 1, "sink registration on streaming");
                Assert.Equal(0, h.Host.Removes);

                connection.FailReads(); // yank
                WaitUntil(() => h.Host.Removes == 1, "sink removal on disconnect");
            }
        }

        [Fact]
        public void AStalledWriterNeverBlocksTheRenderThreadAndFramesCoalesce()
        {
            using (var h = new Harness())
            using (var gate = new ManualResetEventSlim(false))
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                // Let the handshake and the connect Brightness through
                // first (Hello + Brightness = 2 writes), then wedge the
                // writer for everything that follows.
                WaitUntil(() => connection.WriteAttempts >= 2, "the connect sequence");
                connection.WriteGate = gate; // unsignalled: writes now stall

                byte[] a = PatternFrame(1);
                byte[] b = PatternFrame(2);
                byte[] c = PatternFrame(3);
                h.Manager.OnFrame(a, 0);
                // The pump claims frame A and stalls inside its write.
                WaitUntil(() => connection.WriteAttempts >= 3, "the stalled frame write");

                var sw = Stopwatch.StartNew();
                h.Manager.OnFrame(b, 2);
                h.Manager.OnFrame(c, 4);
                sw.Stop();
                // The render-thread contract: one bounded copy, never a wait
                // on the wedged socket (the 5 s fake write stall would blow
                // this bound many times over if OnFrame blocked).
                Assert.True(
                    sw.ElapsedMilliseconds < 1000,
                    $"OnFrame must not block on a stalled writer (took {sw.ElapsedMilliseconds} ms)");

                gate.Set(); // unwedge: A's write completes, then the newest pending frame
                WaitUntil(
                    () => TxPackets(connection).OfType<FramePacket>().Count() >= 2,
                    "the stalled and the coalesced frame");
                List<FramePacket> frames = TxPackets(connection).OfType<FramePacket>().ToList();
                // Newest-frame-wins: B was overwritten by C while the writer
                // was wedged — only A (already claimed) and C reach the wire.
                Assert.Equal(2, frames.Count);
                Assert.Equal(a, frames[0].Pixels);
                Assert.Equal(c, frames[1].Pixels);
            }
        }

        [Fact]
        public void OpenRetriesWithBackoffThroughTheStaleHandleWindow()
        {
            using (var h = new Harness())
            {
                // Windows-replug shape: access denied twice while the stale
                // COM handle lingers, then the open succeeds.
                h.Factory.EnqueueFailure("Access to the port 'COM7' is denied.");
                h.Factory.EnqueueFailure("Access to the port 'COM7' is denied.");
                h.Factory.EnqueueConnection(AnsweringConnection());
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "streaming after open retries");
                Assert.True(h.Factory.OpenAttempts >= 3, "expected at least three open attempts");
                Assert.All(h.Factory.OpenedPorts, port => Assert.Equal(DevicePort, port));
            }
        }

        [Fact]
        public void ManualOverrideBypassesDiscoveryButStillHandshakes()
        {
            using (var h = new Harness())
            {
                // Discovery has nothing to offer: only a foreign device.
                h.Enumerator.SetPorts(new SerialPortInfo("COM3", 0x0403, 0x6001));
                var connection = new FakeConnection("COM9");
                connection.EnqueueRx(HelloAckWire());
                h.Factory.EnqueueConnection(connection);

                h.Manager.ManualPortOverride = "COM9";
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "streaming on the override port");
                Assert.Equal("COM9", h.Manager.Status.PortName);
                Assert.Contains("COM9", h.Factory.OpenedPorts);
                // The override path never runs the native enumeration — the
                // scan stays off the hot path entirely.
                Assert.Equal(0, h.Enumerator.Scans);
                // ...and the handshake still ran (Hello on the wire).
                Assert.Contains(TxPackets(connection), p => p is HelloPacket);
            }
        }

        [Fact]
        public void OverrideAppliedWhileStreamingReconnectsToTheNewPort()
        {
            using (var h = new Harness())
            {
                FakeConnection first = AnsweringConnection();
                h.Factory.EnqueueConnection(first);
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming,
                    "the initial streaming session");

                var second = new FakeConnection("COM9");
                second.EnqueueRx(HelloAckWire());
                h.Factory.EnqueueConnection(second);
                h.Manager.ManualPortOverride = "COM9";

                // The override names a different port: the session unwinds
                // cleanly and the manager reconnects on the override.
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming
                        && h.Manager.Status.PortName == "COM9",
                    "the reconnect onto the override port");
                Assert.True(first.Disposed, "the displaced session must release its port");
                // ...with a full handshake of its own on the new port.
                Assert.Contains(TxPackets(second), p => p is HelloPacket);
            }
        }

        [Fact]
        public void OverrideAppliedDuringTheConnectWindowStillWins()
        {
            using (var h = new Harness())
            {
                // Auto-discovery nominates COM7, but the device answers the
                // handshake only after the override has landed — the whole
                // Apply happens inside the Connecting window (in production
                // that window is multi-second: open-retry backoff plus the
                // handshake deadline).
                var stale = new FakeConnection(DevicePort);
                h.Factory.EnqueueConnection(stale);
                h.Manager.Start();
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Connecting,
                    "the connect window on the auto-discovered port");

                var overrideConnection = new FakeConnection("COM9");
                overrideConnection.EnqueueRx(HelloAckWire());
                h.Factory.EnqueueConnection(overrideConnection);
                h.Manager.ManualPortOverride = "COM9";

                // Only now does COM7 answer. Without the session-side
                // override reconcile the manager would publish Streaming on
                // COM7 and stay there until the next unplug.
                stale.EnqueueRx(HelloAckWire());
                WaitUntil(
                    () => h.Manager.Status.State == DeviceConnectionState.Streaming
                        && h.Manager.Status.PortName == "COM9",
                    "streaming on the override port");
                Assert.True(stale.Disposed, "the stale session must be torn down");
            }
        }

        [Fact]
        public void RapidBrightnessChangesCoalesceUnderTheRateGateAndTheTrailingValueLands()
        {
            DeviceConnectionOptions options = FastOptions();
            options.MinBrightnessIntervalMs = 200; // nonzero: exercise the real gate
            using (var h = new Harness(options))
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(
                    () => TxPackets(connection).Any(p => p is BrightnessPacket),
                    "the (re)connect Brightness send");

                // A slider-drag burst well inside one rate-limit interval:
                // the depth-one slot must coalesce it, and once the interval
                // elapses the newest value must land with no further input
                // (the guaranteed trailing send).
                for (byte v = 1; v <= 20; v++)
                {
                    h.Policy.SetDirect(v);
                }
                WaitUntil(
                    () => TxPackets(connection).OfType<BrightnessPacket>().Any(p => p.Value == 20),
                    "the trailing coalesced Brightness send");

                List<BrightnessPacket> sends =
                    TxPackets(connection).OfType<BrightnessPacket>().ToList();
                Assert.Equal(80, sends[0].Value); // the connect-time send
                Assert.Equal(20, sends[sends.Count - 1].Value); // the final dragged value
                // Between them the gate held: 20 changes may not produce 20
                // packets (one mid-burst send is tolerated in case a slow CI
                // machine stretches the burst across an interval boundary).
                Assert.True(
                    sends.Count <= 3,
                    $"a 20-step drag inside one interval must coalesce (saw {sends.Count} sends)");
            }
        }

        [Fact]
        public void StopReleasesThePortAndIsIdempotent()
        {
            using (var h = new Harness())
            {
                FakeConnection connection = AnsweringConnection();
                h.Factory.EnqueueConnection(connection);
                h.Manager.Start();
                WaitUntil(() => h.Host.Adds == 1, "sink registration on streaming");

                h.Manager.Stop();
                Assert.True(connection.Disposed, "Stop must close the port");
                Assert.Equal(DeviceConnectionState.Stopped, h.Manager.Status.State);
                Assert.Equal(h.Host.Adds, h.Host.Removes);

                h.Manager.Stop(); // idempotent — SimHub may End more than once
                h.Manager.OnFrame(PatternFrame(9), 0); // and late frames are no-ops
                Assert.Equal(DeviceConnectionState.Stopped, h.Manager.Status.State);
            }
        }
    }
}
