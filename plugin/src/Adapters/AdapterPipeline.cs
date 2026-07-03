// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Adapter orchestration (docs/v2-plan.md M4 step 2): the generic adapter
// always runs first, then any registered game-specific refiners whose
// Matches accepts the current game — so M10's iRacing raw-telemetry
// adapter plugs in by construction, no pipeline changes needed.

using System;
using Uniflag.Rendering;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Runs a <see cref="TelemetrySnapshot"/> through the adapter chain and
    /// yields the <see cref="RenderState"/> for the renderer. Immutable
    /// after construction and allocation-free per <see cref="Map"/> call
    /// (it runs on SimHub's update thread at ~60 Hz).
    /// </summary>
    public sealed class AdapterPipeline
    {
        private readonly IGameAdapter[] _adapters;

        /// <summary>The production pipeline: generic adapter only (until M10).</summary>
        public AdapterPipeline()
            : this(Array.Empty<IGameAdapter>())
        {
        }

        /// <summary>
        /// Pipeline with game-specific <paramref name="refiners"/> layered
        /// after the built-in <see cref="GenericAdapter"/>, applied in the
        /// given order to every snapshot whose game they match.
        /// </summary>
        public AdapterPipeline(params IGameAdapter[] refiners)
        {
            if (refiners == null)
            {
                throw new ArgumentNullException(nameof(refiners));
            }
            _adapters = new IGameAdapter[refiners.Length + 1];
            _adapters[0] = new GenericAdapter();
            Array.Copy(refiners, 0, _adapters, 1, refiners.Length);
        }

        /// <summary>
        /// Map one snapshot: start from <see cref="RenderState.Default"/>,
        /// let every matching adapter write its verdict in chain order.
        /// </summary>
        public RenderState Map(TelemetrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            RenderState state = RenderState.Default;
            for (int i = 0; i < _adapters.Length; i++)
            {
                if (_adapters[i].Matches(snapshot.GameName))
                {
                    _adapters[i].Map(snapshot, ref state);
                }
            }
            return state;
        }
    }
}
