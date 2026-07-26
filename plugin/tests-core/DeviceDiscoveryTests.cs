// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Candidate-selection tests. The USB identity filter lives in the platform
// enumerator (it matches registry key names built from the constants pinned
// here); selection itself is pure — override wins outright, otherwise the
// lowest-ordinal enumerated port, and the handshake remains the final
// confirmation (tested elsewhere).

using Uniflag.Device;
using Xunit;

namespace Uniflag.Tests
{
    public class DeviceDiscoveryTests
    {
        [Fact]
        public void ConstantsMirrorTheProtoCrate()
        {
            // proto/src/packet.rs: USB_VID = 0x1209, USB_PID = 0x0001 (test),
            // registered PID 0xF1A6 pending (docs/protocol.md §Transport).
            Assert.Equal(0x1209, DeviceDiscovery.UsbVendorId);
            Assert.Equal(0x0001, DeviceDiscovery.UsbProductIdTest);
            Assert.Equal(0xF1A6, DeviceDiscovery.UsbProductIdRegistered);
        }

        [Fact]
        public void ManualOverrideBypassesEnumeration()
        {
            // Override wins even when discovery has a legitimate candidate,
            // and names a port discovery never enumerated at all (the escape
            // hatch for a panel the identity filter cannot see).
            var ports = new[] { "COM5" };
            Assert.Equal("COM9", DeviceDiscovery.SelectCandidate(ports, "COM9"));
            Assert.Equal("COM9", DeviceDiscovery.SelectCandidate(null, " COM9 ")); // trimmed, no scan needed
        }

        [Fact]
        public void EmptyOrWhitespaceOverrideMeansAutoDiscovery()
        {
            var ports = new[] { "COM5" };
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, ""));
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, "   "));
        }

        [Fact]
        public void NoEnumeratedPortsMeansNoCandidate()
        {
            Assert.Null(DeviceDiscovery.SelectCandidate(new string[0], null));
            Assert.Null(DeviceDiscovery.SelectCandidate(null, null));
        }

        [Fact]
        public void PicksTheLowestOrdinalPortWhenSeveralMatch()
        {
            // Arbitrary but stable across scans — reconnects must not
            // ping-pong between two attached devices.
            var ports = new[] { "COM8", "COM5" };
            Assert.Equal("COM5", DeviceDiscovery.SelectCandidate(ports, null));
        }
    }
}
