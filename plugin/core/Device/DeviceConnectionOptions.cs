// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag.Device
{
    /// <summary>
    /// Timing knobs for <see cref="DeviceConnectionManager"/>. Production
    /// uses <see cref="Default"/>; tests shrink everything so lifecycle
    /// transitions complete in milliseconds. All values are in
    /// milliseconds.
    /// </summary>
    public sealed class DeviceConnectionOptions
    {
        /// <summary>
        /// Pause between discovery scans while no device is connected. The
        /// scan is the only place the native port enumeration runs — never
        /// per frame.
        /// </summary>
        public int ScanIntervalMs { get; set; } = 2000;

        /// <summary>Deadline for the HelloAck after sending Hello.</summary>
        public int HandshakeTimeoutMs { get; set; } = 2000;

        /// <summary>
        /// Pause before re-attempting a device that refused the handshake
        /// (version/panel mismatch). Longer than the scan interval: the
        /// refusal is sticky-by-nature (only a firmware or plugin update
        /// fixes it) and the status text should stay readable, not flicker.
        /// </summary>
        public int RefusedRetryMs { get; set; } = 5000;

        /// <summary>
        /// TX pump idle poll: the upper bound on how long a pending-but-
        /// rate-limited brightness value or a stop request waits for the
        /// pump to notice it when no frame wake-ups are arriving.
        /// </summary>
        public int TxIdlePollMs { get; set; } = 250;

        /// <summary>
        /// Minimum interval between Brightness packets on the wire. The
        /// depth-one pending slot always holds the newest value, so a
        /// dragging slider coalesces to at most one send per interval and
        /// the final value is always delivered (trailing send).
        /// </summary>
        public int MinBrightnessIntervalMs { get; set; } = 100;

        /// <summary>Production defaults.</summary>
        public static DeviceConnectionOptions Default => new DeviceConnectionOptions();
    }
}
