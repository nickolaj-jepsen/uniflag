// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag.Device
{
    /// <summary>
    /// One enumerated serial port with the USB identity it enumerated under.
    /// Ports that carry no USB identity at all (legacy UARTs, virtual ports)
    /// are simply not enumerated — they could never pass the discovery
    /// filter, and the manual port override does not go through enumeration.
    /// </summary>
    public sealed class SerialPortInfo
    {
        /// <summary>The OS port name, e.g. <c>COM5</c>.</summary>
        public string PortName { get; }

        public ushort VendorId { get; }

        public ushort ProductId { get; }

        public SerialPortInfo(string portName, ushort vendorId, ushort productId)
        {
            PortName = portName;
            VendorId = vendorId;
            ProductId = productId;
        }
    }
}
