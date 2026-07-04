// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag.Device
{
    /// <summary>
    /// One enumerated serial port with its USB identity, when the platform
    /// enumeration could correlate it. Ports that exist but could not be
    /// correlated to a USB VID/PID (legacy UARTs, virtual ports) carry null
    /// ids — they never pass the discovery filter, but a manual port
    /// override can still target them.
    /// </summary>
    public sealed class SerialPortInfo
    {
        /// <summary>The OS port name, e.g. <c>COM5</c>.</summary>
        public string PortName { get; }

        /// <summary>USB vendor id, or null when uncorrelated.</summary>
        public ushort? VendorId { get; }

        /// <summary>USB product id, or null when uncorrelated.</summary>
        public ushort? ProductId { get; }

        public SerialPortInfo(string portName, ushort? vendorId, ushort? productId)
        {
            PortName = portName;
            VendorId = vendorId;
            ProductId = productId;
        }
    }
}
