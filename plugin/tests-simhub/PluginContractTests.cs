// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Reflection;
using SimHub.Plugins;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// The plugin type exposes exactly the contract SimHub's loader scans for.
    /// </summary>
    public class PluginContractTests
    {
        [Fact]
        public void ImplementsSimHubPluginInterfaces()
        {
            var t = typeof(UniflagPlugin);
            Assert.True(typeof(IPlugin).IsAssignableFrom(t));
            Assert.True(typeof(IDataPlugin).IsAssignableFrom(t));
            Assert.True(typeof(IWPFSettingsV2).IsAssignableFrom(t));
        }

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
