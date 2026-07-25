// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

namespace Uniflag.Protocol
{
    /// <summary>
    /// Assigned packet type bytes (mirrors <c>proto::packet::PacketType</c>).
    /// Host→device types have the high bit clear, device→host types have it
    /// set. The byte space is open: receivers ignore packets with unassigned
    /// type bytes (see <see cref="UnknownPacket"/>) — never an error.
    /// </summary>
    public enum PacketType : byte
    {
        /// <summary>Host→device handshake open; payload <c>[protocol_version: u8]</c>.</summary>
        Hello = 0x01,

        /// <summary>Host→device full panel; payload 3072 B RGB888, row-major from top-left.</summary>
        Frame = 0x02,

        /// <summary>Host→device brightness multiplier; payload <c>[value: u8]</c>.</summary>
        Brightness = 0x03,

        /// <summary>Device→host handshake reply; payload <c>[proto_ver][width][height][fw ASCII tail]</c>.</summary>
        HelloAck = 0x81,

        /// <summary>Device→host button press; payload <c>[button: u8][kind: u8]</c>.</summary>
        ButtonEvent = 0x82,
    }

    /// <summary>
    /// Button ids carried in <see cref="ButtonEventPacket"/> (mirrors
    /// <c>proto::packet::Button</c>). The id space is open — unassigned
    /// bytes still parse (forward compat), so interpret via
    /// <see cref="ProtocolIds.ButtonFromByte"/> and ignore <c>null</c>.
    /// </summary>
    public enum Button : byte
    {
        /// <summary>GPIO 21 — brightness up.</summary>
        BrightnessUp = 0,

        /// <summary>GPIO 26 — brightness down.</summary>
        BrightnessDown = 1,

        /// <summary>GPIO 27 — sleep.</summary>
        Sleep = 2,
    }

    /// <summary>
    /// Press kinds carried in <see cref="ButtonEventPacket"/> (mirrors
    /// <c>proto::packet::PressKind</c>). Classification happens on release:
    /// a long press never <i>also</i> fires a short press. Open id space —
    /// interpret via <see cref="ProtocolIds.PressKindFromByte"/>.
    /// </summary>
    public enum PressKind : byte
    {
        Short = 0,

        Long = 1,
    }

    /// <summary>
    /// Byte↔enum helpers for the open id spaces. C# enums admit any
    /// underlying value, so a plain cast would silently "know" unassigned
    /// ids — these helpers make the assigned/unassigned distinction explicit
    /// (the analogue of the Rust <c>from_byte -&gt; Option</c> functions).
    /// </summary>
    public static class ProtocolIds
    {
        /// <summary>The assigned <see cref="PacketType"/> for a type byte, or <c>null</c> if unassigned.</summary>
        public static PacketType? PacketTypeFromByte(byte value)
        {
            switch (value)
            {
                case (byte)PacketType.Hello: return PacketType.Hello;
                case (byte)PacketType.Frame: return PacketType.Frame;
                case (byte)PacketType.Brightness: return PacketType.Brightness;
                case (byte)PacketType.HelloAck: return PacketType.HelloAck;
                case (byte)PacketType.ButtonEvent: return PacketType.ButtonEvent;
                default: return null;
            }
        }

        /// <summary>The assigned <see cref="Button"/> for an id byte, or <c>null</c> if unassigned.</summary>
        public static Button? ButtonFromByte(byte value)
        {
            switch (value)
            {
                case (byte)Button.BrightnessUp: return Button.BrightnessUp;
                case (byte)Button.BrightnessDown: return Button.BrightnessDown;
                case (byte)Button.Sleep: return Button.Sleep;
                default: return null;
            }
        }

        /// <summary>The assigned <see cref="PressKind"/> for an id byte, or <c>null</c> if unassigned.</summary>
        public static PressKind? PressKindFromByte(byte value)
        {
            switch (value)
            {
                case (byte)PressKind.Short: return PressKind.Short;
                case (byte)PressKind.Long: return PressKind.Long;
                default: return null;
            }
        }
    }
}
