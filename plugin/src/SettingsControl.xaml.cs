// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System.Windows.Controls;

namespace Uniflag
{
    /// <summary>
    /// Settings tab stub: device status placeholder, disabled brightness
    /// slider, empty 32x32 preview. Wired up across M3 (preview), M9 (device
    /// status + brightness).
    /// </summary>
    public partial class SettingsControl : UserControl
    {
        public SettingsControl()
        {
            InitializeComponent();
        }
    }
}
