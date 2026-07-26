// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Protocol
{
    /// <summary>
    /// One CRC-validated raw packet, split into its type byte and payload —
    /// the C# analogue of the Rust <c>packet::parse_raw</c> return. The type
    /// byte is kept raw so callers can ignore unknown types explicitly
    /// (forward compat) rather than erroring here.
    /// </summary>
    public readonly struct RawPacket
    {
        public byte Type { get; }

        public byte[] Payload { get; }

        public RawPacket(byte type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }
    }

    /// <summary>
    /// Binary packet layer for the v2 protocol — port of
    /// <c>proto/src/packet.rs</c>. Wire format, outermost first:
    ///
    /// <code>
    /// wire   := cobs(raw) 0x00          -- one trailing delimiter per packet
    /// raw    := type payload crc16      -- crc16 little-endian, over type+payload
    /// type   := u8
    /// </code>
    ///
    /// The CRC (<see cref="Crc16"/>, CRC-16/CCITT-FALSE) is computed over
    /// the raw bytes <i>before</i> COBS so it also guards against COBS-layer
    /// bugs, and appended little-endian.
    ///
    /// Forward-compat posture: a receiver <b>ignores packets with unknown
    /// type bytes</b> (<see cref="UnknownPacket"/> — never an error) and
    /// <b>drops packets with bad CRCs</b>, resynchronizing on the next
    /// <c>0x00</c> delimiter. A <i>known</i> type with a wrong-length
    /// payload is also dropped (<see cref="ProtocolErrorKind.BadLength"/>).
    ///
    /// The Rust <c>proto::packet</c> module is where the payload layouts are
    /// defined, mirrored in prose by <c>docs/protocol.md</c> and in bytes by
    /// the golden vectors under <c>testdata/proto/</c>. Any wire-visible
    /// change bumps <see cref="ProtocolVersion"/> and updates all of them
    /// together.
    /// </summary>
    public static class PacketCodec
    {
        /// <summary>
        /// Protocol version carried in the Hello / HelloAck handshake. The
        /// plugin requires exact equality and refuses to drive the device on
        /// mismatch — bump on any wire-visible change.
        /// </summary>
        public const byte ProtocolVersion = 1;

        public const int PanelWidth = 32;

        public const int PanelHeight = 32;

        /// <summary>Frame payload: RGB888, row-major from the top-left, 3 bytes per pixel.</summary>
        public const int FramePayloadLength = PanelWidth * PanelHeight * 3;

        /// <summary>Largest raw packet: type byte + Frame payload + CRC.</summary>
        public const int MaxRawLength = 1 + FramePayloadLength + 2;

        /// <summary>
        /// Largest on-the-wire packet, including the trailing <c>0x00</c>
        /// delimiter (<c>Cobs.MaxEncodedLength(MaxRawLength) + 1</c>, spelled
        /// out as const arithmetic).
        /// </summary>
        public const int MaxWireLength = MaxRawLength + MaxRawLength / 254 + 1 + 1;

        /// <summary>Fixed payload length of <see cref="HelloPacket"/>.</summary>
        public const int HelloPayloadLength = 1;

        /// <summary>Fixed payload length of <see cref="BrightnessPacket"/>.</summary>
        public const int BrightnessPayloadLength = 1;

        /// <summary>Fixed payload length of <see cref="ButtonEventPacket"/>.</summary>
        public const int ButtonEventPayloadLength = 2;

        /// <summary>Minimum payload length of <see cref="HelloAckPacket"/> — the 3-byte header without the variable firmware-version tail.</summary>
        public const int HelloAckMinPayloadLength = 3;

        /// <summary>
        /// Encoder-side cap on the HelloAck firmware-version string,
        /// mirroring the Rust encoder. <b>Not</b> a wire limit — parsers
        /// accept any length the framing allows.
        /// </summary>
        public const int MaxFwVersionLength = 32;

        /// <summary>The inter-packet wire delimiter.</summary>
        public const byte Delimiter = 0x00;

        /// <summary>Serialize the raw form: <c>[type][payload][crc16 LE]</c>.</summary>
        public static byte[] WriteRaw(byte type, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            var raw = new byte[1 + payload.Length + 2];
            raw[0] = type;
            Array.Copy(payload, 0, raw, 1, payload.Length);
            ushort checksum = Crc16.Checksum(raw, 0, 1 + payload.Length);
            raw[1 + payload.Length] = (byte)(checksum & 0xFF);
            raw[2 + payload.Length] = (byte)(checksum >> 8);
            return raw;
        }

        /// <summary>
        /// Full wire encode: COBS over the raw form, then the trailing
        /// <c>0x00</c> delimiter.
        /// </summary>
        public static byte[] EncodeWire(byte type, byte[] payload)
        {
            byte[] body = Cobs.Encode(WriteRaw(type, payload));
            var wire = new byte[body.Length + 1];
            Array.Copy(body, wire, body.Length);
            wire[body.Length] = Delimiter;
            return wire;
        }

        /// <summary>
        /// Validate a COBS-decoded raw packet and split it into type byte +
        /// payload. Throws <see cref="ProtocolErrorKind.TooShort"/> below
        /// type + CRC size and <see cref="ProtocolErrorKind.BadCrc"/> on
        /// checksum mismatch.
        ///
        /// The payload length is <b>not</b> validated against the type — a
        /// valid-CRC Frame with 5 bytes of payload parses fine here. The
        /// typed layer (<see cref="ParsePacket(byte[], int, int)"/> /
        /// <see cref="FromPayload"/>) is what enforces the declared layouts.
        /// </summary>
        public static RawPacket ParseRaw(byte[] raw, int offset, int count)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }
            if (offset < 0 || count < 0 || offset + count > raw.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (count < 3)
            {
                throw new ProtocolException(
                    ProtocolErrorKind.TooShort,
                    "raw packet shorter than type + CRC");
            }
            int bodyLength = count - 2;
            ushort expected = (ushort)(raw[offset + bodyLength] | (raw[offset + bodyLength + 1] << 8));
            if (Crc16.Checksum(raw, offset, bodyLength) != expected)
            {
                throw new ProtocolException(
                    ProtocolErrorKind.BadCrc,
                    "CRC mismatch - drop the packet and resync");
            }
            var payload = new byte[bodyLength - 1];
            Array.Copy(raw, offset + 1, payload, 0, payload.Length);
            return new RawPacket(raw[offset], payload);
        }

        /// <summary>Validate and split a whole raw array.</summary>
        public static RawPacket ParseRaw(byte[] raw)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }
            return ParseRaw(raw, 0, raw.Length);
        }

        /// <summary>
        /// Typed view over a CRC-validated type byte + payload. An
        /// unassigned type byte is <b>not</b> an error — it surfaces as
        /// <see cref="UnknownPacket"/> for the caller to skip explicitly. A
        /// known type with a wrong-length payload throws
        /// <see cref="ProtocolErrorKind.BadLength"/> — receivers drop the
        /// packet like a CRC failure.
        /// </summary>
        public static Packet FromPayload(byte type, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            switch (type)
            {
                case (byte)PacketType.Hello:
                    if (payload.Length != HelloPayloadLength)
                    {
                        throw BadLength(PacketType.Hello, payload.Length);
                    }
                    return new HelloPacket(payload[0]);

                case (byte)PacketType.Frame:
                    if (payload.Length != FramePayloadLength)
                    {
                        throw BadLength(PacketType.Frame, payload.Length);
                    }
                    return new FramePacket(payload);

                case (byte)PacketType.Brightness:
                    if (payload.Length != BrightnessPayloadLength)
                    {
                        throw BadLength(PacketType.Brightness, payload.Length);
                    }
                    return new BrightnessPacket(payload[0]);

                case (byte)PacketType.HelloAck:
                    if (payload.Length < HelloAckMinPayloadLength)
                    {
                        throw BadLength(PacketType.HelloAck, payload.Length);
                    }
                    var fwVersion = new byte[payload.Length - HelloAckMinPayloadLength];
                    Array.Copy(payload, HelloAckMinPayloadLength, fwVersion, 0, fwVersion.Length);
                    return new HelloAckPacket(payload[0], payload[1], payload[2], fwVersion);

                case (byte)PacketType.ButtonEvent:
                    if (payload.Length != ButtonEventPayloadLength)
                    {
                        throw BadLength(PacketType.ButtonEvent, payload.Length);
                    }
                    return new ButtonEventPacket(payload[0], payload[1]);

                default:
                    return new UnknownPacket(type, payload);
            }
        }

        /// <summary>
        /// Validate a raw packet (via <see cref="ParseRaw(byte[], int, int)"/>)
        /// and type it. Framing problems (short, bad CRC) throw; an
        /// unassigned type byte returns <see cref="UnknownPacket"/> — ignore
        /// it, never treat it as an error; a known type with a wrong-length
        /// payload throws <see cref="ProtocolErrorKind.BadLength"/> — drop
        /// it like a CRC failure.
        /// </summary>
        public static Packet ParsePacket(byte[] raw, int offset, int count)
        {
            RawPacket rawPacket = ParseRaw(raw, offset, count);
            return FromPayload(rawPacket.Type, rawPacket.Payload);
        }

        /// <summary>Validate and type a whole raw array.</summary>
        public static Packet ParsePacket(byte[] raw)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }
            return ParsePacket(raw, 0, raw.Length);
        }

        /// <summary>
        /// Decode one delimiter-stripped wire segment: COBS decode, CRC
        /// check, typed parse — the receiver-side pipeline the conformance
        /// vectors are defined against.
        /// </summary>
        public static Packet DecodeWire(byte[] segment, int offset, int count)
        {
            byte[] raw = Cobs.Decode(segment, offset, count);
            return ParsePacket(raw);
        }

        /// <summary>Decode a whole delimiter-stripped wire segment.</summary>
        public static Packet DecodeWire(byte[] segment)
        {
            if (segment == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            return DecodeWire(segment, 0, segment.Length);
        }

        private static ProtocolException BadLength(PacketType type, int actual)
        {
            return new ProtocolException(
                ProtocolErrorKind.BadLength,
                $"{type} payload of {actual} bytes doesn't match its declared layout");
        }
    }
}
