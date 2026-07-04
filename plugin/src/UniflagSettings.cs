// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag
{
    /// <summary>
    /// Persisted via SimHub's common settings store (JSON under SimHub's
    /// PluginsData directory). Owned by the plugin — the device never stores
    /// settings in v2. Properties are mutated in place as the user changes
    /// them; SimHub writes the file when the plugin saves at End.
    /// </summary>
    public class UniflagSettings
    {
        /// <summary>
        /// Panel brightness, 0..=255. Seeds the host-side
        /// <see cref="Uniflag.Device.BrightnessPolicy"/> at Init and is
        /// re-sent to the device on every (re)connect — the value survives
        /// SimHub restarts here, never on the device.
        /// </summary>
        public byte Brightness { get; set; } = 80;

        /// <summary>
        /// Manual COM-port override for device discovery. Empty means
        /// automatic VID/PID discovery; a named port bypasses the USB-id
        /// filter but still has to pass the Hello/HelloAck handshake.
        /// </summary>
        public string ManualPortOverride { get; set; } = "";
    }
}
