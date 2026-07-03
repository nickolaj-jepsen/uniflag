// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace Uniflag
{
    [PluginName("Uniflag")]
    [PluginDescription("Sim-racing flag panel: drives a Pimoroni Cosmic Unicorn over USB plus a browser/overlay virtual panel")]
    [PluginAuthor("Nickolaj Jepsen")]
    public class UniflagPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => null;

        public string LeftMenuTitle => "Uniflag";

        internal UniflagSettings Settings { get; private set; }

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("GeneralSettings", () => new UniflagSettings());
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // M4: feed the game adapters. Runs on SimHub's update thread (~60 Hz),
            // also while no game is running — keep it allocation-light.
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl();
        }
    }
}
