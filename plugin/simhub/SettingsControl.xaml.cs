// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Uniflag.Device;
using Uniflag.Rendering;
using Uniflag.Web;

namespace Uniflag
{
    /// <summary>
    /// Settings tab: device status block, brightness slider driving the
    /// shared <see cref="BrightnessPolicy"/>, manual COM-port override, the
    /// live 32×32 renderer preview, and the web
    /// overlay status line. The preview sink and 1 Hz status poll run only
    /// between Loaded and Unloaded, so the renderer loop and timer are idle
    /// while the tab is not visible. All status updates happen on the UI
    /// thread — the timer is a <see cref="DispatcherTimer"/> and the
    /// device/web servers expose lock-free snapshots.
    /// </summary>
    public partial class SettingsControl : UserControl
    {
        private readonly RendererLoop _renderer;
        private readonly OverlayWebServer _webServer;
        private readonly DeviceConnectionManager _device;
        private readonly BrightnessPolicy _brightness;
        private readonly UniflagSettings _settings;
        private readonly WpfPreviewSink _previewSink;
        private readonly DispatcherTimer _statusTimer;
        private bool _active;

        // Guards the slider feedback loop: true while the status poll pushes
        // the policy's value into the slider, so ValueChanged does not echo
        // it back into the policy.
        private bool _syncingSlider;

        /// <summary>Designer/stub constructor: static tab, no live content.</summary>
        public SettingsControl()
            : this(null, null, null, null, null)
        {
        }

        public SettingsControl(
            RendererLoop renderer,
            OverlayWebServer webServer,
            DeviceConnectionManager device,
            BrightnessPolicy brightness,
            UniflagSettings settings)
        {
            InitializeComponent();
            _renderer = renderer;
            _webServer = webServer;
            _device = device;
            _brightness = brightness;
            _settings = settings;

            if (_webServer != null)
            {
                WebOverlayStatusText.Text = _webServer.StatusText;
            }
            if (_device != null)
            {
                DeviceStatusText.Text = _device.Status.StatusText;
            }
            if (_brightness != null)
            {
                SyncSliderFromPolicy();
            }
            else
            {
                BrightnessSlider.IsEnabled = false;
            }
            if (_settings != null)
            {
                ManualPortTextBox.Text = _settings.ManualPortOverride ?? string.Empty;
            }
            else
            {
                ManualPortTextBox.IsEnabled = false;
                ApplyPortButton.IsEnabled = false;
            }

            if (_webServer != null || _device != null || _brightness != null)
            {
                // Polled, not evented: the servers expose no change
                // notifications. Ticks run on the dispatcher, so every UI
                // touch is already marshalled.
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _statusTimer.Tick += OnStatusTick;
            }

            if (_renderer != null)
            {
                _previewSink = new WpfPreviewSink(Dispatcher);
                PreviewImage.Source = _previewSink.Bitmap;
            }

            if (_renderer != null || _statusTimer != null)
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
            }
            if (_statusTimer != null)
            {
                OnStatusTick(null, null); // fresh text now, not in a second
                _statusTimer.Start();
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
            _statusTimer?.Stop();
            if (_renderer != null)
            {
                _renderer.RemoveSink(_previewSink);
            }
        }

        private void OnStatusTick(object sender, EventArgs e)
        {
            if (_webServer != null)
            {
                WebOverlayStatusText.Text = _webServer.StatusText;
            }
            if (_device != null)
            {
                DeviceStatusText.Text = _device.Status.StatusText;
            }
            // Device buttons move the policy while the tab is open: reflect
            // that into the slider, but never mid-drag (the user's hand
            // wins) and never as a feedback echo into the policy.
            if (_brightness != null && !BrightnessSlider.IsMouseCaptureWithin)
            {
                SyncSliderFromPolicy();
            }
        }

        private void SyncSliderFromPolicy()
        {
            byte current = _brightness.Current;
            if ((byte)Math.Round(BrightnessSlider.Value) == current)
            {
                return;
            }
            _syncingSlider = true;
            try
            {
                BrightnessSlider.Value = current;
            }
            finally
            {
                _syncingSlider = false;
            }
        }

        private void OnBrightnessSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var rounded = (byte)Math.Round(e.NewValue);
            if (BrightnessValueText != null)
            {
                // Fires during InitializeComponent (Value="80" in XAML)
                // before sibling fields are assigned — hence the null check.
                BrightnessValueText.Text = rounded.ToString();
            }
            if (_brightness == null || _syncingSlider)
            {
                return;
            }
            // The connection manager's depth-one slot coalesces the packet
            // sends during a drag.
            _brightness.SetDirect(rounded);
        }

        private void OnApplyPortOverride(object sender, RoutedEventArgs e)
        {
            if (_device == null || _settings == null)
            {
                return;
            }
            string trimmed = (ManualPortTextBox.Text ?? string.Empty).Trim();
            ManualPortTextBox.Text = trimmed;
            _settings.ManualPortOverride = trimmed; // SimHub persists at End
            _device.ManualPortOverride = trimmed;   // empty = auto-discovery
        }

    }
}
