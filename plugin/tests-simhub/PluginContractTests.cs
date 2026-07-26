// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Reflection;
using SimHub.Plugins;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// The plugin type carries the metadata SimHub's loader scans for. The
    /// interfaces it implements are compiler-enforced by the declaration and
    /// need no test; these attributes are not.
    /// </summary>
    public class PluginContractTests
    {
        [Fact]
        public void CarriesPluginMetadataAttributes()
        {
            var t = typeof(UniflagPlugin);
            Assert.NotNull(t.GetCustomAttribute<PluginNameAttribute>());
            Assert.NotNull(t.GetCustomAttribute<PluginDescriptionAttribute>());
            Assert.NotNull(t.GetCustomAttribute<PluginAuthorAttribute>());
        }
    }
}
