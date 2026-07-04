// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Session state, shared by the adapter pipeline and the Grammar renderer
// (docs/flag-grammar.md §9). Host-only — the v2 wire protocol carries
// rendered frames, not state.

namespace Uniflag.Rendering
{
    /// <summary>Game session state, as mapped by the adapters.</summary>
    public enum Session
    {
        PreRace,
        Racing,
        Paused,
        PostRace,
        Replay,
        Unknown,
    }
}
