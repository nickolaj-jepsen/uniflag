// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag
{
    /// <summary>
    /// Persisted via SimHub's common settings store (JSON under SimHub's
    /// PluginsData directory). Owned by the plugin — the device never stores
    /// settings in v2.
    /// </summary>
    public class UniflagSettings
    {
        public byte Brightness { get; set; } = 80;
    }
}
