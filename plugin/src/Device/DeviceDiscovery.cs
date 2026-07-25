// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.Collections.Generic;

namespace Uniflag.Device
{
    /// <summary>
    /// Pure candidate selection for device discovery: given an enumerated
    /// port list and the user's manual override, pick the port to attempt.
    /// The VID/PID filter only <i>nominates</i> — every candidate must still
    /// pass the Hello/HelloAck handshake before being driven.
    /// </summary>
    public static class DeviceDiscovery
    {
        /// <summary>
        /// USB vendor id the device enumerates with — pid.codes' shared
        /// vendor id. Mirrors <c>USB_VID</c> in <c>proto/src/packet.rs</c>
        /// (the cross-language source of truth; docs/protocol.md §Transport).
        /// </summary>
        public const ushort UsbVendorId = 0x1209;

        /// <summary>
        /// The pid.codes <b>test PID</b> current firmware enumerates with.
        /// Mirrors <c>USB_PID</c> in <c>proto/src/packet.rs</c>.
        /// </summary>
        public const ushort UsbProductIdTest = 0x0001;

        /// <summary>
        /// The future registered pid.codes PID (docs/protocol.md §Transport).
        /// The filter accepts both this and <see cref="UsbProductIdTest"/>
        /// during the transition, per the note on <c>USB_PID</c> in
        /// <c>proto/src/packet.rs</c>.
        /// </summary>
        public const ushort UsbProductIdRegistered = 0xF1A6;

        /// <summary>
        /// Whether an enumerated port carries the uniflag USB identity
        /// (either PID during the transition). Uncorrelated ports (null ids)
        /// never match.
        /// </summary>
        public static bool IsUniflagDevice(SerialPortInfo port)
        {
            if (port == null)
            {
                return false;
            }
            return port.VendorId == UsbVendorId
                && (port.ProductId == UsbProductIdTest || port.ProductId == UsbProductIdRegistered);
        }

        /// <summary>
        /// The port name to attempt next, or null when there is nothing to
        /// try. A non-empty <paramref name="manualOverride"/> wins outright
        /// and bypasses the VID/PID filter (the handshake still validates
        /// it); otherwise the lowest-ordinal matching port is returned —
        /// with more than one device attached the choice is arbitrary but
        /// stable across scans.
        /// </summary>
        public static string SelectCandidate(IReadOnlyList<SerialPortInfo> ports, string manualOverride)
        {
            if (!string.IsNullOrWhiteSpace(manualOverride))
            {
                return manualOverride.Trim();
            }
            if (ports == null)
            {
                return null;
            }
            string best = null;
            foreach (SerialPortInfo port in ports)
            {
                if (!IsUniflagDevice(port))
                {
                    continue;
                }
                if (best == null || StringComparer.OrdinalIgnoreCase.Compare(port.PortName, best) < 0)
                {
                    best = port.PortName;
                }
            }
            return best;
        }
    }
}
