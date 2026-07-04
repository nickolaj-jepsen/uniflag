// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// OPT-IN hardware integration test (M9). Gated on the UNIFLAG_DEVICE_PORT
// environment variable: unset (the CI / default case) every test here is
// SKIPPED and no serial port is ever touched. Set it to the device's port
// (e.g. UNIFLAG_DEVICE_PORT=COM5) on a box with a flashed panel attached —
// and nothing else holding the port — to run the real end-to-end
// conversation: open, handshake, 3 s of 30 fps frames, a Brightness, and a
// clean disconnect proven by a successful reopen.

using System;
using System.Diagnostics;
using System.Threading;
using Uniflag.Device;
using Uniflag.Protocol;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// <see cref="FactAttribute"/> that skips unless
    /// <see cref="PortVariable"/> is set — the standard xunit
    /// derived-attribute skip pattern, no extra packages.
    /// </summary>
    public sealed class DeviceFactAttribute : FactAttribute
    {
        /// <summary>Environment variable naming the device's COM port.</summary>
        public const string PortVariable = "UNIFLAG_DEVICE_PORT";

        public DeviceFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PortVariable)))
            {
                Skip = "set " + PortVariable + "=COMx (with a flashed panel attached and the "
                    + "port otherwise free) to run the hardware integration test";
            }
        }
    }

    public class HardwareIntegrationTests
    {
        private const int HandshakeTimeoutMs = 3000;
        private const int StreamMilliseconds = 3000;
        private const int FrameIntervalMs = 33; // ~30 fps, the heartbeat cadence

        [DeviceFact]
        public void HandshakesStreamsThreeSecondsAndDisconnectsCleanly()
        {
            string envPort = Environment.GetEnvironmentVariable(DeviceFactAttribute.PortVariable);

            // Discovery-or-direct-open: prefer the VID/PID scan when it
            // agrees with the environment (exercising the registry
            // correlation end-to-end); otherwise trust the explicit port.
            string target = envPort;
            try
            {
                string discovered = DeviceDiscovery.SelectCandidate(
                    new WindowsRegistryPortEnumerator().EnumeratePorts(), null);
                if (string.Equals(discovered, envPort, StringComparison.OrdinalIgnoreCase))
                {
                    target = discovered;
                }
            }
            catch
            {
                // Enumeration hiccups must not fail the conversation test.
            }

            var factory = new SerialPortConnectionFactory();
            using (ISerialConnection connection = factory.Open(target))
            {
                HandshakeResult result =
                    DeviceHandshake.Perform(connection, HandshakeTimeoutMs, () => false);
                Assert.Equal(HandshakeOutcome.Success, result.Outcome);
                Assert.Equal(PacketCodec.ProtocolVersion, result.Ack.ProtocolVersion);
                Assert.Equal((byte)32, result.Ack.Width);
                Assert.Equal((byte)32, result.Ack.Height);

                // Brightness re-send, as on every (re)connect.
                byte[] brightness = new BrightnessPacket(128).EncodeWire();
                connection.Write(brightness, 0, brightness.Length);

                // 3 s of frames — a moving diagonal so a watching human sees
                // the panel is really being driven.
                var pixels = new byte[PacketCodec.FramePayloadLength];
                var clock = Stopwatch.StartNew();
                int frames = 0;
                while (clock.ElapsedMilliseconds < StreamMilliseconds)
                {
                    Array.Clear(pixels, 0, pixels.Length);
                    int p = frames % 32;
                    pixels[(p * 32 + p) * 3 + 1] = 200; // green pixel down the diagonal
                    byte[] wire = PacketCodec.EncodeWire((byte)PacketType.Frame, pixels);
                    connection.Write(wire, 0, wire.Length);
                    frames++;
                    Thread.Sleep(FrameIntervalMs);
                }
                // Sleep-paced, so expect a little under the ideal 90; well
                // above the ~45 a halved cadence would produce.
                Assert.True(frames >= 60, $"expected >= 60 frames in 3 s, wrote {frames}");
            }

            // Clean disconnect: the handle must actually be released — a
            // fresh open plus a second successful handshake proves both the
            // host teardown and the device's session recovery.
            using (ISerialConnection reopened = factory.Open(target))
            {
                HandshakeResult second =
                    DeviceHandshake.Perform(reopened, HandshakeTimeoutMs, () => false);
                Assert.Equal(HandshakeOutcome.Success, second.Outcome);
            }
        }
    }
}
