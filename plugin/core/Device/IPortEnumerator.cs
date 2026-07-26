// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Collections.Generic;

namespace Uniflag.Device
{
    /// <summary>
    /// Seam over the platform serial-port enumeration. Implementations own
    /// the USB identity filter: they return only ports carrying the uniflag
    /// VID/PID (constants on <see cref="DeviceDiscovery"/>). Production is
    /// <see cref="WindowsRegistryPortEnumerator"/>.
    ///
    /// <para><b>Cost contract:</b> implementations may hit the registry or
    /// other native services — callers must invoke this only from a
    /// background scan (timer / reconnect events), never on the render or
    /// SimHub update threads and never per frame.</para>
    /// </summary>
    public interface IPortEnumerator
    {
        /// <summary>
        /// Names of the currently present ports that carry the uniflag USB
        /// identity. May throw — callers treat a failed scan as "no
        /// candidates this pass" and retry later.
        /// </summary>
        IReadOnlyList<string> EnumeratePorts();
    }
}
