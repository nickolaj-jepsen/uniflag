// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Device
{
    /// <summary>
    /// Seam over one open duplex serial link so the handshake and the
    /// connection manager are testable against in-memory fakes (no real
    /// COM ports in CI). Production is <see cref="SerialPortConnection"/>.
    /// </summary>
    public interface ISerialConnection : IDisposable
    {
        /// <summary>The port this connection was opened on, e.g. <c>COM5</c>.</summary>
        string PortName { get; }

        /// <summary>
        /// Read available bytes. <b>Bounded-block contract:</b> blocks at
        /// most a short implementation-defined interval and returns 0 when
        /// no data arrived — callers poll in a loop and check their own
        /// stop/deadline flags between calls. Throws when the link is dead
        /// (yank, dispose).
        /// </summary>
        int Read(byte[] buffer, int offset, int count);

        /// <summary>
        /// Write the given bytes. <b>Bounded-block contract:</b> a link that
        /// refuses progress must fault (timeout exception) rather than hang
        /// forever, so the TX pump can always unwind. Throws when the link
        /// is dead.
        /// </summary>
        void Write(byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// Factory seam for opening connections; lets tests script connection
    /// sequences (open failures, reconnects). Production is
    /// <see cref="SerialPortConnectionFactory"/>.
    /// </summary>
    public interface ISerialConnectionFactory
    {
        /// <summary>
        /// Open <paramref name="portName"/> exclusively. Throws on failure —
        /// including the access-denied window right after a replug, while
        /// Windows still holds the stale COM handle (callers retry with
        /// backoff).
        /// </summary>
        ISerialConnection Open(string portName);
    }
}
