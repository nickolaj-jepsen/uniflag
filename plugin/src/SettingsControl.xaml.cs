// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Uniflag.Rendering;
using Uniflag.Web;

namespace Uniflag
{
    /// <summary>
    /// Settings tab: device status placeholder and disabled brightness
    /// slider (wired in M9), the live 32×32 renderer preview with its debug
    /// state-cycler (M3), and the web overlay server status line (M5, M9
    /// expands it). The preview sink and the status poll run only between
    /// Loaded and Unloaded, so the renderer loop and the timer are idle
    /// while the tab is not visible.
    /// </summary>
    public partial class SettingsControl : UserControl
    {
        private readonly RendererLoop _renderer;
        private readonly OverlayWebServer _webServer;
        private readonly WpfPreviewSink _previewSink;
        private readonly StateCycler _cycler;
        private readonly DispatcherTimer _webStatusTimer;
        private bool _active;

        /// <summary>Designer/stub constructor: static tab, no live content.</summary>
        public SettingsControl()
            : this(null, null)
        {
        }

        public SettingsControl(RendererLoop renderer, OverlayWebServer webServer)
        {
            InitializeComponent();
            _renderer = renderer;
            _webServer = webServer;

            if (_webServer != null)
            {
                WebOverlayStatusText.Text = _webServer.StatusText;
                // Polled, not evented: the server exposes no change
                // notifications (M9 may add them) and 1 Hz is plenty for a
                // status line that only ticks while the tab is visible.
                _webStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webStatusTimer.Tick += OnWebStatusTick;
            }

            if (_renderer != null)
            {
                _previewSink = new WpfPreviewSink(Dispatcher);
                PreviewImage.Source = _previewSink.Bitmap;
                _cycler = new StateCycler(_renderer);
            }
            else
            {
                CycleStatesCheckBox.IsEnabled = false;
            }

            if (_renderer != null || _webServer != null)
            {
                Loaded += OnLoaded;
                Unloaded += OnUnloaded;
                // Unloaded is not raised when the application window closes —
                // without this, a live timer could keep firing (and root this
                // control) until process exit.
                Dispatcher.ShutdownStarted += OnDispatcherShutdown;
            }
        }

        private void OnDispatcherShutdown(object sender, EventArgs e)
        {
            Teardown();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_active)
            {
                return; // Loaded can re-fire without an intervening Unloaded
            }
            _active = true;
            if (_renderer != null)
            {
                _renderer.AddSink(_previewSink);
                if (CycleStatesCheckBox.IsChecked == true)
                {
                    _cycler.Start();
                }
            }
            if (_webStatusTimer != null)
            {
                OnWebStatusTick(null, null); // fresh text now, not in a second
                _webStatusTimer.Start();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Teardown();
        }

        private void Teardown()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            _webStatusTimer?.Stop();
            if (_renderer != null)
            {
                _cycler.Stop();
                _renderer.RemoveSink(_previewSink);
            }
        }

        private void OnWebStatusTick(object sender, EventArgs e)
        {
            WebOverlayStatusText.Text = _webServer.StatusText;
        }

        private void OnCycleStatesToggled(object sender, RoutedEventArgs e)
        {
            if (_cycler == null || !_active)
            {
                return; // stub instance, or toggled while the tab is unloaded
            }
            if (CycleStatesCheckBox.IsChecked == true)
            {
                _cycler.Start();
            }
            else
            {
                _cycler.Stop();
            }
        }
    }
}
