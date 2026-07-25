// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The web server's seam onto renderer sink registration: OverlayWebServer
// registers itself as a frame sink only while at least one WebSocket client
// is connected, so the render thread runs only when someone is actually
// watching. The interface exists so tests can observe the add/remove refcount
// without a real render thread; production always goes through RendererSinkHost.

using System;
using Uniflag.Rendering;

namespace Uniflag.Web
{
    /// <summary>
    /// The sink-registration surface of <see cref="RendererLoop"/>, as the
    /// web overlay server consumes it.
    /// </summary>
    public interface IFrameSinkHost
    {
        /// <summary>Register a sink; idempotent per sink instance.</summary>
        void AddSink(IFrameSink sink);

        /// <summary>Unregister a sink; unknown sinks are ignored.</summary>
        void RemoveSink(IFrameSink sink);
    }

    /// <summary>
    /// Production adapter: forwards straight to a <see cref="RendererLoop"/>.
    /// </summary>
    public sealed class RendererSinkHost : IFrameSinkHost
    {
        private readonly RendererLoop _renderer;

        public RendererSinkHost(RendererLoop renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        }

        /// <inheritdoc />
        public void AddSink(IFrameSink sink) => _renderer.AddSink(sink);

        /// <inheritdoc />
        public void RemoveSink(IFrameSink sink) => _renderer.RemoveSink(sink);
    }
}
