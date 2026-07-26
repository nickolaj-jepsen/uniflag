// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using Uniflag.Protocol;

namespace Uniflag.Device
{
    /// <summary>Connection lifecycle states of <see cref="DeviceConnectionManager"/>.</summary>
    public enum DeviceConnectionState
    {
        /// <summary>Not started, or stopped (plugin End).</summary>
        Stopped,

        /// <summary>Scanning for a candidate port (nothing attached, or between retries).</summary>
        Scanning,

        /// <summary>Candidate found; opening and handshaking.</summary>
        Connecting,

        /// <summary>Handshake validated; frames are streaming at 30 fps.</summary>
        Streaming,

        /// <summary>
        /// The device answered but was refused (protocol-version or
        /// panel-size mismatch) — surfaced to the user, never driven.
        /// </summary>
        Refused,
    }

    /// <summary>
    /// Immutable snapshot of the device connection for the settings tab.
    /// Published as a whole (one volatile reference) so the 1 Hz UI poll
    /// always reads a consistent state without locking.
    /// </summary>
    public sealed class DeviceStatus
    {
        public DeviceConnectionState State { get; }

        /// <summary>Port in play, or null while scanning with no candidate.</summary>
        public string PortName { get; }

        /// <summary>Firmware version from the HelloAck; null unless streaming.</summary>
        public string FirmwareVersion { get; }

        /// <summary>Device protocol version from the HelloAck; null unless streaming.</summary>
        public byte? ProtocolVersion { get; }

        /// <summary>Panel width from the HelloAck; null unless streaming.</summary>
        public byte? PanelWidth { get; }

        /// <summary>Panel height from the HelloAck; null unless streaming.</summary>
        public byte? PanelHeight { get; }

        /// <summary>
        /// Last failure text: the refusal message in <see cref="DeviceConnectionState.Refused"/>,
        /// otherwise the most recent open/handshake/session error (may be
        /// null).
        /// </summary>
        public string LastError { get; }

        private DeviceStatus(
            DeviceConnectionState state,
            string portName,
            string firmwareVersion,
            byte? protocolVersion,
            byte? panelWidth,
            byte? panelHeight,
            string lastError)
        {
            State = state;
            PortName = portName;
            FirmwareVersion = firmwareVersion;
            ProtocolVersion = protocolVersion;
            PanelWidth = panelWidth;
            PanelHeight = panelHeight;
            LastError = lastError;
        }

        /// <summary>Not started, or stopped (plugin End).</summary>
        public static DeviceStatus Stopped { get; } =
            new DeviceStatus(DeviceConnectionState.Stopped, null, null, null, null, null, null);

        /// <summary>Scanning, with the most recent failure text (may be null).</summary>
        public static DeviceStatus Scanning(string lastError) =>
            new DeviceStatus(DeviceConnectionState.Scanning, null, null, null, null, null, lastError);

        /// <summary>Candidate found; opening and handshaking.</summary>
        public static DeviceStatus Connecting(string portName) =>
            new DeviceStatus(DeviceConnectionState.Connecting, portName, null, null, null, null, null);

        /// <summary>Refuse-with-message: the mismatch text, verbatim.</summary>
        public static DeviceStatus Refused(string portName, string message) =>
            new DeviceStatus(DeviceConnectionState.Refused, portName, null, null, null, null, message);

        /// <summary>Streaming, with the identity the HelloAck carried.</summary>
        public static DeviceStatus Streaming(string portName, HelloAckPacket ack) =>
            new DeviceStatus(
                DeviceConnectionState.Streaming,
                portName,
                ack.FwVersionString,
                ack.ProtocolVersion,
                ack.Width,
                ack.Height,
                null);

        /// <summary>
        /// Human-readable line for the settings tab. Never throws; never
        /// null.
        /// </summary>
        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case DeviceConnectionState.Scanning:
                        return LastError == null
                            ? "Searching for a device..."
                            : "Searching for a device... (" + LastError + ")";
                    case DeviceConnectionState.Connecting:
                        return "Connecting to " + PortName + "...";
                    case DeviceConnectionState.Streaming:
                        return "Connected on " + PortName
                            + " - fw " + (string.IsNullOrEmpty(FirmwareVersion) ? "?" : FirmwareVersion)
                            + ", protocol v" + ProtocolVersion
                            + ", panel " + PanelWidth + "x" + PanelHeight;
                    case DeviceConnectionState.Refused:
                        // The refuse-with-message contract: the mismatch text
                        // is the status, verbatim.
                        return PortName + ": " + LastError;
                    default:
                        return "Stopped";
                }
            }
        }
    }
}
