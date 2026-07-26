// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// COM-port lookup for Windows: "which present COM port is the panel?". The
// registry route (HKLM\SYSTEM\CurrentControlSet\Enum\USB) was chosen over
// WMI/CIM (Win32_PnPEntity): it needs no WMI service round-trip, reads fine
// unelevated, and returns in microseconds — but it is still a native query,
// so the DeviceConnectionManager only calls it from its background scan
// loop, never on a per-frame path.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using Microsoft.Win32;

namespace Uniflag.Device
{
    /// <summary>
    /// Production <see cref="IPortEnumerator"/>. Rather than enumerate every
    /// port and correlate it, this asks the narrow question directly: it
    /// opens only the <c>Enum\USB</c> subkeys whose name already carries the
    /// uniflag VID/PID, reads their <c>Device Parameters\PortName</c>, and
    /// intersects with the ports actually present
    /// (<see cref="SerialPort.GetPortNames"/>, which reads
    /// <c>HARDWARE\DEVICEMAP\SERIALCOMM</c>).
    ///
    /// <para>Enum entries outlive the hardware, so several stale identities
    /// can name the same reassigned port. Filtering by key name first means a
    /// foreign identity is never opened and so can never mask the live
    /// device; the residual error runs the benign way, a stale uniflag entry
    /// nominating a port the handshake then refuses.</para>
    /// </summary>
    public sealed class WindowsRegistryPortEnumerator : IPortEnumerator
    {
        private const string UsbEnumKeyPath = @"SYSTEM\CurrentControlSet\Enum\USB";

        /// <summary>
        /// Enum\USB key-name prefixes for the uniflag identity. Key names
        /// look like <c>VID_1209&amp;PID_0001</c>, sometimes with an
        /// <c>&amp;MI_00</c> suffix (CDC-ACM is an interface-association
        /// composite on some stacks), so this matches on prefix.
        /// </summary>
        private static readonly string[] KeyPrefixes =
        {
            KeyPrefix(DeviceDiscovery.UsbVendorId, DeviceDiscovery.UsbProductIdTest),
            KeyPrefix(DeviceDiscovery.UsbVendorId, DeviceDiscovery.UsbProductIdRegistered),
        };

        private static string KeyPrefix(ushort vid, ushort pid) =>
            string.Format(CultureInfo.InvariantCulture, "VID_{0:X4}&PID_{1:X4}", vid, pid);

        /// <inheritdoc />
        public IReadOnlyList<string> EnumeratePorts()
        {
            var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(UsbEnumKeyPath))
            {
                if (usb == null)
                {
                    return found;
                }
                foreach (string deviceKeyName in usb.GetSubKeyNames())
                {
                    if (!MatchesUniflagIdentity(deviceKeyName))
                    {
                        continue;
                    }
                    CollectInstances(usb, deviceKeyName, present, seen, found);
                }
            }
            return found;
        }

        private static bool MatchesUniflagIdentity(string deviceKeyName)
        {
            for (int i = 0; i < KeyPrefixes.Length; i++)
            {
                if (deviceKeyName.StartsWith(KeyPrefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CollectInstances(
            RegistryKey usb,
            string deviceKeyName,
            HashSet<string> present,
            HashSet<string> seen,
            List<string> found)
        {
            using (RegistryKey device = usb.OpenSubKey(deviceKeyName))
            {
                if (device == null)
                {
                    return;
                }
                foreach (string instance in device.GetSubKeyNames())
                {
                    string portName;
                    using (RegistryKey parameters = device.OpenSubKey(instance + @"\Device Parameters"))
                    {
                        portName = parameters?.GetValue("PortName") as string;
                    }
                    // Only present ports are candidates, and one entry per
                    // port is enough.
                    if (portName == null || !present.Contains(portName) || !seen.Add(portName))
                    {
                        continue;
                    }
                    found.Add(portName);
                }
            }
        }
    }
}
