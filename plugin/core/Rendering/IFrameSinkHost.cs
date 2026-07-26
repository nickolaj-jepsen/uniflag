// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The sink-registration half of RendererLoop. Its two consumers (overlay
// server, device manager) register only while someone is watching, so the
// render thread runs on demand rather than all session.
//
// An interface so both can be tested without a real 60 Hz render thread: a
// concrete RendererLoop in a device test would pump live frames into the
// fake serial port and make the TX assertions nondeterministic.

namespace Uniflag.Rendering
{
    /// <summary>
    /// The sink-registration surface of <see cref="RendererLoop"/>, which
    /// implements it directly.
    /// </summary>
    public interface IFrameSinkHost
    {
        /// <summary>Register a sink; idempotent per sink instance.</summary>
        void AddSink(IFrameSink sink);

        /// <summary>Unregister a sink; unknown sinks are ignored.</summary>
        void RemoveSink(IFrameSink sink);
    }
}
