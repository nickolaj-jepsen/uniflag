// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Pure-logic tests for the Windows registry port enumerator: the
// Enum\USB key-name VID/PID parsing (both PIDs, composite-interface shapes,
// foreign devices, malformed ids) and the identity-collision tie-break that
// keeps a stale foreign registry entry from masking the live device. No
// registry access and no serial ports — these reach the internal seams via
// InternalsVisibleTo.

using Uniflag.Device;
using Xunit;

namespace Uniflag.Tests
{
    public class WindowsRegistryPortEnumeratorTests
    {
        // ---------------------------------------------------------------
        // TryParseVidPid: Enum\USB subkey-name shapes.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("VID_1209&PID_0001", 0x1209, 0x0001)] // test PID
        [InlineData("VID_1209&PID_F1A6", 0x1209, 0xF1A6)] // registered PID
        [InlineData("VID_1209&PID_0001&MI_00", 0x1209, 0x0001)] // composite CDC-ACM interface
        [InlineData("VID_1209&PID_F1A6&REV_0100&MI_00", 0x1209, 0xF1A6)]
        [InlineData("vid_1209&pid_0001", 0x1209, 0x0001)] // tokens are case-insensitive
        [InlineData("VID_0403&PID_6001", 0x0403, 0x6001)] // foreign identities parse too
        public void ParsesVidPidTokensFromEnumUsbKeyNames(string keyName, int vid, int pid)
        {
            Assert.True(
                WindowsRegistryPortEnumerator.TryParseVidPid(keyName, out ushort v, out ushort p));
            Assert.Equal((ushort)vid, v);
            Assert.Equal((ushort)pid, p);
        }

        [Theory]
        [InlineData("ROOT_HUB30")] // hubs/controllers carry no VID/PID tokens
        [InlineData("VID_1209")] // VID only
        [InlineData("PID_0001")] // PID only
        [InlineData("VID_120&PID_0001")] // short VID hex ("120&" is not hex)
        [InlineData("VID_ZZZZ&PID_0001")] // non-hex VID
        [InlineData("VID_1209&PID_00")] // PID truncated by the end of the key
        [InlineData("")]
        public void RejectsKeyNamesWithoutBothWellFormedTokens(string keyName)
        {
            Assert.False(WindowsRegistryPortEnumerator.TryParseVidPid(keyName, out _, out _));
        }

        // ---------------------------------------------------------------
        // PreferCandidate: several registry identities recording the same
        // present port name (stale Enum entries after COM-number reuse).
        // ---------------------------------------------------------------

        private static SerialPortInfo Uniflag(ushort pid)
        {
            return new SerialPortInfo("COM5", DeviceDiscovery.UsbVendorId, pid);
        }

        private static SerialPortInfo Foreign()
        {
            return new SerialPortInfo("COM5", 0x0483, 0x5740); // STM32 VCP
        }

        [Fact]
        public void UniflagIdentityDisplacesAStaleForeignCorrelation()
        {
            // GetSubKeyNames returns keys sorted, so a stale lower-sorting
            // foreign entry (VID_0483 < VID_1209) is walked first. First-wins
            // would let it claim the port on every scan and the live device
            // would never be nominated — the uniflag identity must displace
            // it (either PID during the transition).
            Assert.True(WindowsRegistryPortEnumerator.PreferCandidate(
                Foreign(), Uniflag(DeviceDiscovery.UsbProductIdTest)));
            Assert.True(WindowsRegistryPortEnumerator.PreferCandidate(
                Foreign(), Uniflag(DeviceDiscovery.UsbProductIdRegistered)));
        }

        [Fact]
        public void UniflagCorrelationIsNeverDisplaced()
        {
            Assert.False(WindowsRegistryPortEnumerator.PreferCandidate(
                Uniflag(DeviceDiscovery.UsbProductIdTest), Foreign()));
            Assert.False(WindowsRegistryPortEnumerator.PreferCandidate(
                Uniflag(DeviceDiscovery.UsbProductIdRegistered), Foreign()));
        }

        [Fact]
        public void ForeignCollisionsStayFirstWins()
        {
            var ftdi = new SerialPortInfo("COM5", 0x0403, 0x6001);
            Assert.False(WindowsRegistryPortEnumerator.PreferCandidate(Foreign(), ftdi));
        }

        [Fact]
        public void DuplicateUniflagIdentitiesStayFirstWins()
        {
            // Stable nomination: two uniflag instances recording the same
            // port must not flip-flop between scans.
            Assert.False(WindowsRegistryPortEnumerator.PreferCandidate(
                Uniflag(DeviceDiscovery.UsbProductIdTest),
                Uniflag(DeviceDiscovery.UsbProductIdRegistered)));
        }
    }
}
