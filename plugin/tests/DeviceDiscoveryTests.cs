// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Discovery filter tests: the VID/PID nomination logic with injected
// fake port entries. Both the test PID and the future registered PID must
// pass during the transition; foreign identities and uncorrelated ports
// must not; a manual override bypasses the filter entirely (the handshake
// remains the final confirmation, tested elsewhere).

using Uniflag.Device;
using Xunit;

namespace Uniflag.Tests
{
    public class DeviceDiscoveryTests
    {
        private static SerialPortInfo Port(string name, ushort? vid, ushort? pid)
        {
            return new SerialPortInfo(name, vid, pid);
        }

        [Fact]
        public void ConstantsMirrorTheProtoCrate()
        {
            // proto/src/packet.rs: USB_VID = 0x1209, USB_PID = 0x0001 (test),
            // registered PID 0xF1A6 pending (docs/v2-tracking.md).
            Assert.Equal(0x1209, DeviceDiscovery.UsbVendorId);
            Assert.Equal(0x0001, DeviceDiscovery.UsbProductIdTest);
            Assert.Equal(0xF1A6, DeviceDiscovery.UsbProductIdRegistered);
        }

        [Fact]
        public void AcceptsTheTestPid()
        {
            var ports = new[] { Port("COM5", 0x1209, 0x0001) };
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, null));
        }

        [Fact]
        public void AcceptsTheRegisteredPid()
        {
            var ports = new[] { Port("COM6", 0x1209, 0xF1A6) };
            Assert.Equal("COM6", DeviceDiscovery.SelectCandidate(ports, null));
        }

        [Fact]
        public void RejectsForeignVendorEvenWithAMatchingProductId()
        {
            var ports = new[]
            {
                Port("COM3", 0x2341, 0x0001), // Arduino VID, uniflag PID byte
                Port("COM4", 0x0403, 0x6001), // FTDI
            };
            Assert.Null(DeviceDiscovery.SelectCandidate(ports, null));
        }

        [Fact]
        public void RejectsUniflagVendorWithForeignProductId()
        {
            // pid.codes is a shared vendor id — plenty of other projects
            // live under 0x1209; only our two PIDs nominate.
            var ports = new[] { Port("COM3", 0x1209, 0xBEEF) };
            Assert.Null(DeviceDiscovery.SelectCandidate(ports, null));
        }

        [Fact]
        public void RejectsUncorrelatedPorts()
        {
            // Ports the platform enumeration could not tie to a USB identity
            // never pass the filter (manual override is the escape hatch).
            var ports = new[] { Port("COM1", null, null) };
            Assert.Null(DeviceDiscovery.SelectCandidate(ports, null));
        }

        [Fact]
        public void ManualOverrideBypassesTheFilter()
        {
            // Override wins even when discovery has a legitimate candidate,
            // and even when the named port carries no uniflag identity.
            var ports = new[]
            {
                Port("COM5", 0x1209, 0x0001),
                Port("COM9", null, null),
            };
            Assert.Equal("COM9", DeviceDiscovery.SelectCandidate(ports, "COM9"));
            Assert.Equal("COM9", DeviceDiscovery.SelectCandidate(null, " COM9 ")); // trimmed, no scan needed
        }

        [Fact]
        public void EmptyOrWhitespaceOverrideMeansAutoDiscovery()
        {
            var ports = new[] { Port("COM5", 0x1209, 0x0001) };
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, ""));
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, "   "));
        }

        [Fact]
        public void PicksTheLowestOrdinalPortWhenSeveralMatch()
        {
            // Arbitrary but stable across scans — reconnects must not
            // ping-pong between two attached devices.
            var ports = new[]
            {
                Port("COM8", 0x1209, 0xF1A6),
                Port("COM5", 0x1209, 0x0001),
            };
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, null));
        }
    }
}
