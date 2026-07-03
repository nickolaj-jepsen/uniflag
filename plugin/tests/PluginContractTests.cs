// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Reflection;
using SimHub.Plugins;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// Smoke tests for the M1 skeleton: the plugin type exposes exactly the
    /// contract SimHub's loader scans for. Real suites arrive with the
    /// renderer (M3) and codec (M6).
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
