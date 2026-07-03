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

        // M2a throwaway (delete with Spike/ once docs/web-overlay.md has its
        // verdict): serves the overlay test page's WS frames on
        // ws://127.0.0.1:8972/ws. Real page hosting is designed in M5.
        private Spike.OverlaySpikeServer _spike;

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("GeneralSettings", () => new UniflagSettings());
            _spike = new Spike.OverlaySpikeServer();
            _spike.Start(8972);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // M4: feed the game adapters. Runs on SimHub's update thread (~60 Hz),
            // also while no game is running — keep it allocation-light.
        }

        public void End(PluginManager pluginManager)
        {
            _spike?.Stop();
            _spike = null;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl();
        }
    }
}
