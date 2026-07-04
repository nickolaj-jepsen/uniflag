// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag
{
    /// <summary>
    /// Persisted via SimHub's common settings store. Owned by the plugin —
    /// the device never stores settings in v2. SimHub writes the file when
    /// the plugin saves at End.
    /// </summary>
    public class UniflagSettings
    {
        /// <summary>
        /// Panel brightness, 0..=255. Seeds
        /// <see cref="Uniflag.Device.BrightnessPolicy"/> at Init and re-sent
        /// on every (re)connect — persists across SimHub restarts here, never
        /// on the device.
        /// </summary>
        public byte Brightness { get; set; } = 80;

        /// <summary>
        /// Manual COM-port override. Empty means automatic VID/PID discovery;
        /// a named port bypasses the USB-id filter but still has to pass the
        /// Hello/HelloAck handshake.
        /// </summary>
        public string ManualPortOverride { get; set; } = "";
    }
}
