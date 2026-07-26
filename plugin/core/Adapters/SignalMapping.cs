// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Telemetry → SignalState, the whole of it. The generic unified-flag mapping
// always runs; a sim whose raw telemetry says more than the unified layer
// gets a refiner layered after it. There is one refiner (iRacing), so this
// is one `if` — when LMU or F1 lands, add another.

using Uniflag.Rendering.Grammar;

namespace Uniflag.Adapters
{
    /// <summary>
    /// Pure and allocation-free per call: <see cref="Map"/> runs on SimHub's
    /// update thread at ~60 Hz, and must not retain the snapshot. Refiners
    /// run after the generic mapping over the same state, overwriting only
    /// the fields they know better.
    /// </summary>
    public static class SignalMapping
    {
        public static SignalState Map(TelemetrySnapshot snapshot)
        {
            SignalState state = GenericAdapter.Map(snapshot);
            if (IRacingAdapter.Matches(snapshot.GameName))
            {
                IRacingAdapter.Map(snapshot, ref state);
            }
            return state;
        }
    }
}
