// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Windows;
using System.Windows.Controls;
using Uniflag.Rendering;

namespace Uniflag
{
    /// <summary>
    /// Settings tab: device status placeholder and disabled brightness
    /// slider (wired in M9), plus the live 32×32 renderer preview with its
    /// debug state-cycler (M3). The preview sink is registered on Loaded
    /// and unregistered on Unloaded, so the renderer loop only runs while
    /// the tab is actually visible; the cycler follows the checkbox within
    /// that window.
    /// </summary>
    public partial class SettingsControl : UserControl
    {
        private readonly RendererLoop _renderer;
        private readonly WpfPreviewSink _previewSink;
        private readonly StateCycler _cycler;
        private bool _previewActive;

        /// <summary>Designer/stub constructor: static tab, no live preview.</summary>
        public SettingsControl()
            : this(null)
        {
        }

        public SettingsControl(RendererLoop renderer)
        {
            InitializeComponent();
            _renderer = renderer;
            if (_renderer == null)
            {
                CycleStatesCheckBox.IsEnabled = false;
                return;
            }
            _previewSink = new WpfPreviewSink(Dispatcher);
            PreviewImage.Source = _previewSink.Bitmap;
            _cycler = new StateCycler(_renderer);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            // Unloaded is not raised when the application window closes —
            // without this, a live 2 s cycler timer could keep firing (and
            // root this control) until process exit.
            Dispatcher.ShutdownStarted += OnDispatcherShutdown;
        }

        private void OnDispatcherShutdown(object sender, System.EventArgs e)
        {
            Teardown();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_previewActive)
            {
                return; // Loaded can re-fire without an intervening Unloaded
            }
            _previewActive = true;
            _renderer.AddSink(_previewSink);
            if (CycleStatesCheckBox.IsChecked == true)
            {
                _cycler.Start();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Teardown();
        }

        private void Teardown()
        {
            if (!_previewActive)
            {
                return;
            }
            _previewActive = false;
            _cycler.Stop();
            _renderer.RemoveSink(_previewSink);
        }

        private void OnCycleStatesToggled(object sender, RoutedEventArgs e)
        {
            if (_cycler == null || !_previewActive)
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
