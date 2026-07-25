// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.Text;

namespace Uniflag.Protocol
{
    /// <summary>
    /// Typed view of one packet — the C# analogue of the Rust
    /// <c>proto::packet::Packet</c> enum plus its <c>Parsed::Unknown</c>
    /// escape hatch (<see cref="UnknownPacket"/>). Subclasses document their
    /// payload layout; wrong-length payloads for known types never
    /// construct a packet (<see cref="PacketCodec.ParsePacket(byte[])"/>
    /// throws <see cref="ProtocolErrorKind.BadLength"/>).
    ///
    /// Packets compare by value (payload bytes included) so conformance
    /// tests can assert decoded == expected directly.
    /// </summary>
    public abstract class Packet : IEquatable<Packet>
    {
        /// <summary>The wire type byte of this packet.</summary>
        public abstract byte TypeByte { get; }

        /// <summary>
        /// Serialize the declared payload layout for this packet. Throws
        /// <see cref="ProtocolException"/> (<see cref="ProtocolErrorKind.BadLength"/>)
        /// only for a <see cref="HelloAckPacket"/> whose <c>fw_version</c>
        /// exceeds the encoder cap.
        /// </summary>
        public abstract byte[] BuildPayload();

        /// <summary>Raw form: <c>[type][payload][crc16 LE]</c>.</summary>
        public byte[] EncodeRaw()
        {
            return PacketCodec.WriteRaw(TypeByte, BuildPayload());
        }

        /// <summary>Wire form: COBS(raw) plus the single trailing <c>0x00</c> delimiter.</summary>
        public byte[] EncodeWire()
        {
            return PacketCodec.EncodeWire(TypeByte, BuildPayload());
        }

        public abstract bool Equals(Packet other);

        public override bool Equals(object obj)
        {
            return Equals(obj as Packet);
        }

        public abstract override int GetHashCode();

        /// <summary>Sequence equality for payload byte arrays (both non-null).</summary>
        protected static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Host→device, <c>0x01</c>. Opens the handshake on every (re)connect;
    /// carries the host's <see cref="PacketCodec.ProtocolVersion"/>.
    /// Payload: exactly 1 byte, <c>[protocol_version]</c>.
    /// </summary>
    public sealed class HelloPacket : Packet
    {
        public byte ProtocolVersion { get; }

        public HelloPacket(byte protocolVersion)
        {
            ProtocolVersion = protocolVersion;
        }

        public override byte TypeByte => (byte)PacketType.Hello;

        public override byte[] BuildPayload()
        {
            return new[] { ProtocolVersion };
        }

        public override bool Equals(Packet other)
        {
            return other is HelloPacket p && p.ProtocolVersion == ProtocolVersion;
        }

        public override int GetHashCode()
        {
            return (TypeByte << 8) ^ ProtocolVersion;
        }
    }

    /// <summary>
    /// Host→device, <c>0x02</c>. One full panel: RGB888, row-major from the
    /// top-left, 3 bytes per pixel (pixel <c>(x, y)</c> channel <c>c</c> at
    /// offset <c>(y*32 + x)*3 + c</c>, channels R, G, B). Payload: exactly
    /// <see cref="PacketCodec.FramePayloadLength"/> bytes.
    /// </summary>
    public sealed class FramePacket : Packet
    {
        /// <summary>
        /// The 3072 payload bytes. Owned by this packet — not defensively
        /// copied (a full frame every tick at 30 fps; treat as immutable).
        /// </summary>
        public byte[] Pixels { get; }

        public FramePacket(byte[] pixels)
        {
            if (pixels == null)
            {
                throw new ArgumentNullException(nameof(pixels));
            }
            if (pixels.Length != PacketCodec.FramePayloadLength)
            {
                throw new ArgumentException(
                    $"Frame payload must be exactly {PacketCodec.FramePayloadLength} bytes, got {pixels.Length}",
                    nameof(pixels));
            }
            Pixels = pixels;
        }

        public override byte TypeByte => (byte)PacketType.Frame;

        public override byte[] BuildPayload()
        {
            return Pixels;
        }

        public override bool Equals(Packet other)
        {
            return other is FramePacket p && BytesEqual(p.Pixels, Pixels);
        }

        public override int GetHashCode()
        {
            int hash = TypeByte;
            foreach (byte b in Pixels)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }

    /// <summary>
    /// Host→device, <c>0x03</c>. Display brightness multiplier
    /// <c>0..=255</c>, applied by the device pre-gamma to each channel as
    /// <c>(c * (value + 1)) &gt;&gt; 8</c>. Payload: exactly 1 byte,
    /// <c>[value]</c>.
    /// </summary>
    public sealed class BrightnessPacket : Packet
    {
        public byte Value { get; }

        public BrightnessPacket(byte value)
        {
            Value = value;
        }

        public override byte TypeByte => (byte)PacketType.Brightness;

        public override byte[] BuildPayload()
        {
            return new[] { Value };
        }

        public override bool Equals(Packet other)
        {
            return other is BrightnessPacket p && p.Value == Value;
        }

        public override int GetHashCode()
        {
            return (TypeByte << 8) ^ Value;
        }
    }

    /// <summary>
    /// Device→host, <c>0x81</c>. Handshake reply. Payload: at least
    /// <see cref="PacketCodec.HelloAckMinPayloadLength"/> bytes —
    /// <c>[protocol_version][width][height][fw_version…]</c>.
    /// </summary>
    public sealed class HelloAckPacket : Packet
    {
        public byte ProtocolVersion { get; }

        /// <summary>Panel width in pixels (32 on the Cosmic Unicorn).</summary>
        public byte Width { get; }

        /// <summary>Panel height in pixels (32 on the Cosmic Unicorn).</summary>
        public byte Height { get; }

        /// <summary>
        /// Firmware version: ASCII, no NUL terminator, may be empty. No wire
        /// length limit on parse; <see cref="BuildPayload"/> enforces the
        /// encoder-side <see cref="PacketCodec.MaxFwVersionLength"/> cap.
        /// </summary>
        public byte[] FwVersion { get; }

        public HelloAckPacket(byte protocolVersion, byte width, byte height, byte[] fwVersion)
        {
            if (fwVersion == null)
            {
                throw new ArgumentNullException(nameof(fwVersion));
            }
            ProtocolVersion = protocolVersion;
            Width = width;
            Height = height;
            FwVersion = fwVersion;
        }

        /// <summary><see cref="FwVersion"/> decoded as ASCII.</summary>
        public string FwVersionString => Encoding.ASCII.GetString(FwVersion);

        public override byte TypeByte => (byte)PacketType.HelloAck;

        public override byte[] BuildPayload()
        {
            if (FwVersion.Length > PacketCodec.MaxFwVersionLength)
            {
                throw new ProtocolException(
                    ProtocolErrorKind.BadLength,
                    $"fw_version exceeds the encoder cap of {PacketCodec.MaxFwVersionLength} bytes");
            }
            var payload = new byte[PacketCodec.HelloAckMinPayloadLength + FwVersion.Length];
            payload[0] = ProtocolVersion;
            payload[1] = Width;
            payload[2] = Height;
            Array.Copy(FwVersion, 0, payload, PacketCodec.HelloAckMinPayloadLength, FwVersion.Length);
            return payload;
        }

        public override bool Equals(Packet other)
        {
            return other is HelloAckPacket p
                && p.ProtocolVersion == ProtocolVersion
                && p.Width == Width
                && p.Height == Height
                && BytesEqual(p.FwVersion, FwVersion);
        }

        public override int GetHashCode()
        {
            int hash = (TypeByte << 24) ^ (ProtocolVersion << 16) ^ (Width << 8) ^ Height;
            foreach (byte b in FwVersion)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }

    /// <summary>
    /// Device→host, <c>0x82</c>. Payload: exactly 2 bytes,
    /// <c>[button][kind]</c>. Both stay raw bytes so unassigned ids pass
    /// through parsing (forward compat) — interpret via
    /// <see cref="ProtocolIds.ButtonFromByte"/> /
    /// <see cref="ProtocolIds.PressKindFromByte"/>.
    /// </summary>
    public sealed class ButtonEventPacket : Packet
    {
        public byte Button { get; }

        public byte Kind { get; }

        public ButtonEventPacket(byte button, byte kind)
        {
            Button = button;
            Kind = kind;
        }

        public override byte TypeByte => (byte)PacketType.ButtonEvent;

        public override byte[] BuildPayload()
        {
            return new[] { Button, Kind };
        }

        public override bool Equals(Packet other)
        {
            return other is ButtonEventPacket p && p.Button == Button && p.Kind == Kind;
        }

        public override int GetHashCode()
        {
            return (TypeByte << 16) ^ (Button << 8) ^ Kind;
        }
    }

    /// <summary>
    /// A CRC-valid packet whose type byte is unassigned — the analogue of
    /// the Rust <c>Parsed::Unknown</c>. <b>Not an error</b>: forward compat
    /// says receivers pass such packets through parsing and ignore them
    /// explicitly.
    /// </summary>
    public sealed class UnknownPacket : Packet
    {
        private readonly byte _typeByte;

        /// <summary>The verbatim payload bytes (owned by this packet).</summary>
        public byte[] Payload { get; }

        public UnknownPacket(byte typeByte, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            _typeByte = typeByte;
            Payload = payload;
        }

        public override byte TypeByte => _typeByte;

        public override byte[] BuildPayload()
        {
            return Payload;
        }

        public override bool Equals(Packet other)
        {
            return other is UnknownPacket p && p.TypeByte == TypeByte && BytesEqual(p.Payload, Payload);
        }

        public override int GetHashCode()
        {
            int hash = TypeByte;
            foreach (byte b in Payload)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }
}
