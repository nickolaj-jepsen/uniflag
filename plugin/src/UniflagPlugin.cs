// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using Uniflag.Adapters;
using Uniflag.Rendering;
using Uniflag.Web;

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

        /// <summary>
        /// The 60 fps rendering core (M3). Created in Init, disposed in End;
        /// its thread only runs while at least one sink is registered (the
        /// settings tab's preview, or the web overlay while a browser/dash
        /// client is connected).
        /// </summary>
        internal RendererLoop Renderer { get; private set; }

        /// <summary>
        /// The web overlay host (M5): serves the LED-dot page and the frame
        /// WebSocket on http://127.0.0.1:8972/. Started in Init — a bind
        /// failure becomes a settings-tab status, never a crash — and
        /// stopped in End (SimHub rebuilds plugins at every game change, so
        /// the port must come back cleanly).
        /// </summary>
        internal OverlayWebServer WebServer { get; private set; }

        // Telemetry path (M4): one reused snapshot + an immutable pipeline —
        // zero avoidable allocation on the 60 Hz update thread.
        private readonly TelemetrySnapshot _snapshot = new TelemetrySnapshot();
        private readonly AdapterPipeline _adapters = new AdapterPipeline();

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("GeneralSettings", () => new UniflagSettings());
            Renderer = new RendererLoop();
            WebServer = new OverlayWebServer(new RendererSinkHost(Renderer));
            WebServer.Start(OverlayWebServer.DefaultPort);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Runs on SimHub's update thread (~60 Hz), including while no
            // game is running. Everything is copied out of `data` by the
            // extractor; the ref is never stored.
            RendererLoop renderer = Renderer;
            if (renderer == null)
            {
                return; // End() raced the update thread during teardown
            }
            GameDataExtractor.Extract(ref data, _snapshot);
            if (!_snapshot.HasLiveSession)
            {
                // No live game session — the predicate is GameRunning &&
                // !GameInMenu && NewData != null (TelemetrySnapshot
                // .HasLiveSession): menus and process-only detection carry
                // no usable flag state, so show the §7b connected-idle
                // marker. This feeds the NORMAL channel; the settings-tab
                // cycler's override keeps winning if active.
                renderer.SetConnectedIdle();
                return;
            }
            renderer.SetState(_adapters.Map(_snapshot), connected: true);
        }

        public void End(PluginManager pluginManager)
        {
            // Server first: it unregisters its sink from the renderer.
            WebServer?.Stop();
            WebServer = null;
            Renderer?.Dispose();
            Renderer = null;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(Renderer, WebServer);
        }
    }
}
