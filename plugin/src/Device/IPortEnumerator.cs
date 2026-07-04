// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Collections.Generic;

namespace Uniflag.Device
{
    /// <summary>
    /// Seam over the platform serial-port enumeration so discovery filtering
    /// (<see cref="DeviceDiscovery"/>) is testable with injected fake port
    /// lists. Production is <see cref="WindowsRegistryPortEnumerator"/>.
    ///
    /// <para><b>Cost contract:</b> implementations may hit the registry or
    /// other native services — callers must invoke this only from a
    /// background scan (timer / reconnect events), never on the render or
    /// SimHub update threads and never per frame.</para>
    /// </summary>
    public interface IPortEnumerator
    {
        /// <summary>
        /// Snapshot of the currently present serial ports with their USB
        /// identity where known. May throw — callers treat a failed scan as
        /// "no candidates this pass" and retry later.
        /// </summary>
        IReadOnlyList<SerialPortInfo> EnumeratePorts();
    }
}
