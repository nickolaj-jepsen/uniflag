// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// COM-port ↔ USB-identity correlation for Windows. The registry route
// (HKLM\SYSTEM\CurrentControlSet\Enum\USB) was chosen over WMI/CIM
// (Win32_PnPEntity): it needs no WMI service round-trip, reads fine
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
    /// Production <see cref="IPortEnumerator"/>: correlates the currently
    /// present COM ports (<see cref="SerialPort.GetPortNames"/>, which reads
    /// <c>HARDWARE\DEVICEMAP\SERIALCOMM</c>) with USB VID/PID by walking
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&amp;PID_yyyy\…\Device
    /// Parameters\PortName</c>.
    ///
    /// <para>Registry Enum entries persist for unplugged devices, so every
    /// correlation is intersected with the present-port set. Several stale
    /// identities can still record the same (since reassigned) port name;
    /// when they collide, the uniflag identity wins (see
    /// <see cref="PreferCandidate"/>) — a stale foreign entry must never
    /// mask the live device from discovery, while the false-positive
    /// direction stays acceptable because the Hello/HelloAck handshake is
    /// the final confirmation of every candidate.</para>
    /// </summary>
    public sealed class WindowsRegistryPortEnumerator : IPortEnumerator
    {
        private const string UsbEnumKeyPath = @"SYSTEM\CurrentControlSet\Enum\USB";

        /// <inheritdoc />
        public IReadOnlyList<SerialPortInfo> EnumeratePorts()
        {
            var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            var byPort = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);

            using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(UsbEnumKeyPath))
            {
                if (usb != null)
                {
                    foreach (string deviceKeyName in usb.GetSubKeyNames())
                    {
                        // Key names look like "VID_1209&PID_0001" (plus
                        // "&MI_00" for composite interfaces — CDC-ACM is an
                        // interface-association composite on some stacks, so
                        // parse the tokens rather than the exact shape).
                        if (!TryParseVidPid(deviceKeyName, out ushort vid, out ushort pid))
                        {
                            continue;
                        }
                        CollectInstances(usb, deviceKeyName, vid, pid, present, byPort);
                    }
                }
            }

            var result = new List<SerialPortInfo>(byPort.Values);

            // Present ports with no USB correlation (legacy UARTs, virtual
            // ports, or Enum shapes this walk doesn't cover): surface them
            // with null ids so a manual override can still target them.
            foreach (string port in present)
            {
                if (!byPort.ContainsKey(port))
                {
                    result.Add(new SerialPortInfo(port, null, null));
                }
            }
            return result;
        }

        private static void CollectInstances(
            RegistryKey usb,
            string deviceKeyName,
            ushort vid,
            ushort pid,
            HashSet<string> present,
            Dictionary<string, SerialPortInfo> byPort)
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
                    // Intersect with the live port set: Enum entries outlive
                    // the hardware, and only present ports are candidates.
                    if (portName == null || !present.Contains(portName))
                    {
                        continue;
                    }
                    var candidate = new SerialPortInfo(portName, vid, pid);
                    if (!byPort.TryGetValue(portName, out SerialPortInfo existing)
                        || PreferCandidate(existing, candidate))
                    {
                        byPort[portName] = candidate;
                    }
                }
            }
        }

        /// <summary>
        /// Identity-collision tie-break: whether <paramref name="candidate"/>
        /// should replace <paramref name="existing"/> when both registry
        /// identities record the same present port name. GetSubKeyNames
        /// returns keys sorted, so with first-wins a stale lower-sorting
        /// foreign entry (VID_0483, VID_10C4, …) would deterministically
        /// claim the port on every scan and the live device would never be
        /// nominated at all — a false negative the handshake can never
        /// repair. Preferring the uniflag identity flips any residual error
        /// into the benign direction: a stale <i>uniflag</i> entry over a
        /// foreign device gets nominated and then refused by the handshake.
        /// </summary>
        internal static bool PreferCandidate(SerialPortInfo existing, SerialPortInfo candidate)
        {
            return DeviceDiscovery.IsUniflagDevice(candidate)
                && !DeviceDiscovery.IsUniflagDevice(existing);
        }

        /// <summary>
        /// Extract <c>VID_hhhh</c> / <c>PID_hhhh</c> tokens from an Enum\USB
        /// subkey name. Returns false for keys without both tokens (e.g.
        /// <c>ROOT_HUB30</c>).
        /// </summary>
        internal static bool TryParseVidPid(string keyName, out ushort vid, out ushort pid)
        {
            vid = 0;
            pid = 0;
            return TryParseToken(keyName, "VID_", out vid) && TryParseToken(keyName, "PID_", out pid);
        }

        private static bool TryParseToken(string keyName, string token, out ushort value)
        {
            value = 0;
            int at = keyName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + token.Length + 4 > keyName.Length)
            {
                return false;
            }
            string hex = keyName.Substring(at + token.Length, 4);
            return ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
