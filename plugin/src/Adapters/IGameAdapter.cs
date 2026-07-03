// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The layering seam of the adapter architecture (docs/v2-plan.md M4 step 2):
// AdapterPipeline runs every matching adapter in order over the same
// RenderState, so a game-specific adapter (M10's iRacing raw-telemetry
// adapter, keyed on GameName) can sit after the generic one and override or
// refine whatever the unified Flag_* mapping produced.

using Uniflag.Rendering;

namespace Uniflag.Adapters
{
    /// <summary>
    /// One stage of the snapshot → <see cref="RenderState"/> mapping.
    /// Implementations must be pure (state in, state out — no I/O, no
    /// retained references to the snapshot) and allocation-free per call:
    /// <see cref="Map"/> runs on SimHub's update thread at ~60 Hz.
    /// </summary>
    public interface IGameAdapter
    {
        /// <summary>
        /// Whether this adapter applies to <paramref name="gameName"/>
        /// (SimHub's <c>GameData.GameName</c>; may be null or empty when no
        /// game has ever been detected). The generic adapter matches every
        /// game; a raw-telemetry adapter matches exactly its sim.
        /// </summary>
        bool Matches(string gameName);

        /// <summary>
        /// Map or refine: write this adapter's verdict into
        /// <paramref name="state"/>. Called with the result of every earlier
        /// pipeline stage — a game-specific adapter may overwrite only the
        /// fields it knows better (e.g. wave level, caution) and keep the
        /// generic result for the rest.
        /// </summary>
        void Map(TelemetrySnapshot snapshot, ref RenderState state);
    }
}
