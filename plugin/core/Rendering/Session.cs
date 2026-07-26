// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Session state, shared by the adapters and the Grammar renderer
// (docs/flag-grammar.md §9). Host-only — the v2 wire protocol carries
// rendered frames, not state.
//
// Only words an adapter can actually produce: the renderer distinguishes
// Racing/Paused from everything else, so an unmappable word is invisible.

namespace Uniflag.Rendering
{
    /// <summary>Game session state, as mapped by the adapters.</summary>
    public enum Session
    {
        PreRace,
        Racing,
        Paused,
        Unknown,
    }
}
