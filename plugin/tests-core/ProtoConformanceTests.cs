// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Cross-language golden-vector conformance suite — the C# mirror of
// proto/tests/golden_vectors.rs. Both suites load the exact frozen files
// under testdata/proto/ (described by manifest.json); neither side
// generates its own fixtures. The Rust suite additionally asserts the
// files are byte-identical to their generator, so the expectation tables
// here mirror the same single source of truth.

using System;
using System.Collections.Generic;
using System.Text;
using Uniflag.Protocol;
using Xunit;
using Xunit.Sdk;

namespace Uniflag.Tests
{
    /// <summary>Unit tests for <see cref="Crc16"/>, mirroring <c>proto/src/crc.rs</c>.</summary>
    public class Crc16Tests
    {
        [Fact]
        public void CheckValueMatchesCrc16CcittFalse()
        {
            // The catalogue "check" value: CRC-16/CCITT-FALSE of "123456789".
            Assert.Equal(0x29B1, Crc16.Checksum(Encoding.ASCII.GetBytes("123456789")));
        }

        [Fact]
        public void EmptyInputIsInit()
        {
            Assert.Equal(Crc16.Init, Crc16.Checksum(new byte[0]));
        }

        [Fact]
        public void StreamingEqualsOneShot()
        {
            byte[] data = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
            ushort oneShot = Crc16.Checksum(data);
            for (int split = 0; split <= data.Length; split++)
            {
                ushort head = Crc16.Update(Crc16.Init, data, 0, split);
                Assert.Equal(oneShot, Crc16.Update(head, data, split, data.Length - split));
            }
        }

        [Fact]
        public void DetectsSingleBitFlips()
        {
            var data = new byte[64];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }
            ushort good = Crc16.Checksum(data);
            for (int i = 0; i < data.Length; i++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    data[i] ^= (byte)(1 << bit);
                    Assert.True(Crc16.Checksum(data) != good, $"flip at byte {i} bit {bit} not caught");
                    data[i] ^= (byte)(1 << bit);
                }
            }
        }
    }

    /// <summary>Unit tests for <see cref="Cobs"/>, mirroring <c>proto/src/cobs.rs</c>.</summary>
    public class CobsTests
    {
        private static void RoundTrip(byte[] payload)
        {
            byte[] encoded = Cobs.Encode(payload);
            Assert.True(
                encoded.Length <= Cobs.MaxEncodedLength(payload.Length),
                $"bound violated for len {payload.Length}");
            Assert.True(
                Array.IndexOf(encoded, (byte)0) < 0,
                "encoded output contains 0x00");
            byte[] decoded = Cobs.Decode(encoded);
            Assert.True(
                BytesHelper.SequenceEqual(decoded, payload),
                $"round trip failed for len {payload.Length}");
        }

        [Fact]
        public void EmptyPayloadEncodesToASingleGroupHeader()
        {
            Assert.Equal(new byte[] { 0x01 }, Cobs.Encode(new byte[0]));
            RoundTrip(new byte[0]);
        }

        [Fact]
        public void KnownVectors()
        {
            // From the COBS paper / Wikipedia examples.
            var cases = new[]
            {
                (new byte[] { 0x00 }, new byte[] { 0x01, 0x01 }),
                (new byte[] { 0x00, 0x00 }, new byte[] { 0x01, 0x01, 0x01 }),
                (new byte[] { 0x11, 0x22, 0x00, 0x33 }, new byte[] { 0x03, 0x11, 0x22, 0x02, 0x33 }),
                (new byte[] { 0x11, 0x22, 0x33, 0x44 }, new byte[] { 0x05, 0x11, 0x22, 0x33, 0x44 }),
                (new byte[] { 0x11, 0x00, 0x00, 0x00 }, new byte[] { 0x02, 0x11, 0x01, 0x01, 0x01 }),
            };
            foreach (var (payload, expected) in cases)
            {
                Assert.Equal(expected, Cobs.Encode(payload));
                RoundTrip(payload);
            }
        }

        [Fact]
        public void AllZeroPayloads()
        {
            for (int len = 1; len <= 520; len++)
            {
                RoundTrip(new byte[len]);
            }
            Assert.Equal(new byte[] { 1, 1, 1, 1 }, Cobs.Encode(new byte[] { 0, 0, 0 }));
        }

        [Fact]
        public void FullGroupRuns()
        {
            // Exactly 254 non-zero bytes: full group then the canonical
            // (Cheshire & Baker Listing 1) trailing empty group header. A
            // Wikipedia-derived encoder omits the trailing 0x01 and fails
            // here.
            var payload254 = new byte[254];
            for (int i = 0; i < payload254.Length; i++)
            {
                payload254[i] = (byte)(i % 255 + 1);
            }
            byte[] encoded = Cobs.Encode(payload254);
            Assert.Equal(256, encoded.Length);
            Assert.Equal(0xFF, encoded[0]);
            Assert.Equal(0x01, encoded[255]);
            RoundTrip(payload254);

            // The non-canonical form without the trailing header must decode
            // identically (decoder is liberal).
            byte[] decodedShort = Cobs.Decode(encoded, 0, 255);
            Assert.Equal(payload254, decodedShort);

            // 255 non-zero bytes: full group + 1.
            var payload255 = new byte[255];
            for (int i = 0; i < payload255.Length; i++)
            {
                payload255[i] = (byte)(i % 255 + 1);
            }
            encoded = Cobs.Encode(payload255);
            Assert.Equal(257, encoded.Length);
            Assert.Equal(0xFF, encoded[0]);
            Assert.Equal(0x02, encoded[255]);
            RoundTrip(payload255);
        }

        [Fact]
        public void RoundTripsEveryLengthWithMixedContent()
        {
            // Patterned data with zeros sprinkled at varying strides, lengths
            // crossing both group boundaries (254, 508).
            int[] strides = { 1, 3, 7, 254, 255 };
            for (int len = 0; len <= 600; len++)
            {
                foreach (int stride in strides)
                {
                    var payload = new byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        payload[i] = i % stride == 0 ? (byte)0 : (byte)(i % 255 + 1);
                    }
                    RoundTrip(payload);
                }
            }
        }

        [Fact]
        public void FrameSizedRoundTrip()
        {
            // The largest packet the protocol carries.
            var payload = new byte[PacketCodec.MaxRawLength];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 256);
            }
            RoundTrip(payload);
        }

        [Fact]
        public void DecodeRejectsMalformed()
        {
            // Empty input is not a valid encoding.
            AssertMalformed(new byte[0]);
            // Embedded zero in group data.
            AssertMalformed(new byte[] { 0x02, 0x00 });
            // Truncated: code byte promises more data than present.
            AssertMalformed(new byte[] { 0x05, 0x11, 0x22 });
            AssertMalformed(new byte[] { 0xFF, 0x11 });
        }

        private static void AssertMalformed(byte[] encoded)
        {
            var ex = Assert.Throws<ProtocolException>(() => Cobs.Decode(encoded));
            Assert.Equal(ProtocolErrorKind.CobsMalformed, ex.Kind);
        }
    }

    /// <summary>Unit tests for <see cref="PacketCodec"/>, mirroring <c>proto/src/packet.rs</c>.</summary>
    public class PacketCodecTests
    {
        [Fact]
        public void ConstantsAreStable()
        {
            // Wire-frozen numbers; changing any of these is a protocol break.
            Assert.Equal(3072, PacketCodec.FramePayloadLength);
            Assert.Equal(3075, PacketCodec.MaxRawLength);
            Assert.Equal(3089, PacketCodec.MaxWireLength);
            Assert.Equal(PacketCodec.MaxWireLength, Cobs.MaxEncodedLength(PacketCodec.MaxRawLength) + 1);
            Assert.Equal(0x01, (byte)PacketType.Hello);
            Assert.Equal(0x02, (byte)PacketType.Frame);
            Assert.Equal(0x03, (byte)PacketType.Brightness);
            Assert.Equal(0x81, (byte)PacketType.HelloAck);
            Assert.Equal(0x82, (byte)PacketType.ButtonEvent);
            Assert.Equal(1, PacketCodec.HelloPayloadLength);
            Assert.Equal(1, PacketCodec.BrightnessPayloadLength);
            Assert.Equal(2, PacketCodec.ButtonEventPayloadLength);
            Assert.Equal(3, PacketCodec.HelloAckMinPayloadLength);
            Assert.Equal(1, PacketCodec.ProtocolVersion);
            Assert.Equal(0, (byte)Button.BrightnessUp);
            Assert.Equal(1, (byte)Button.BrightnessDown);
            Assert.Equal(2, (byte)Button.Sleep);
            Assert.Equal(0, (byte)PressKind.Short);
            Assert.Equal(1, (byte)PressKind.Long);
        }

        [Fact]
        public void IdBytesRoundTripAndUnassignedOnesDoNot()
        {
            foreach (PacketType ty in new[]
            {
                PacketType.Hello, PacketType.Frame, PacketType.Brightness,
                PacketType.HelloAck, PacketType.ButtonEvent,
            })
            {
                Assert.Equal(ty, ProtocolIds.PacketTypeFromByte((byte)ty));
            }
            Assert.Null(ProtocolIds.PacketTypeFromByte(0x00));
            Assert.Null(ProtocolIds.PacketTypeFromByte(0x7F));
            foreach (Button button in new[] { Button.BrightnessUp, Button.BrightnessDown, Button.Sleep })
            {
                Assert.Equal(button, ProtocolIds.ButtonFromByte((byte)button));
            }
            Assert.Null(ProtocolIds.ButtonFromByte(3));
            foreach (PressKind kind in new[] { PressKind.Short, PressKind.Long })
            {
                Assert.Equal(kind, ProtocolIds.PressKindFromByte((byte)kind));
            }
            Assert.Null(ProtocolIds.PressKindFromByte(2));
        }

        [Fact]
        public void CorruptedBytesFailCrc()
        {
            var payload = new byte[16];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = 0xAA;
            }
            byte[] raw = PacketCodec.WriteRaw((byte)PacketType.Brightness, payload);
            PacketCodec.ParseRaw(raw); // must not throw
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] ^= 0x01;
                var ex = Assert.Throws<ProtocolException>(() => PacketCodec.ParseRaw(raw));
                Assert.True(ex.Kind == ProtocolErrorKind.BadCrc, $"flip at byte {i} not caught as BadCrc");
                raw[i] ^= 0x01;
            }
        }

        [Fact]
        public void TruncatedRawIsRejected()
        {
            foreach (byte[] raw in new[] { new byte[0], new byte[] { 0x02 }, new byte[] { 0x02, 0x00 } })
            {
                var ex = Assert.Throws<ProtocolException>(() => PacketCodec.ParseRaw(raw));
                Assert.Equal(ProtocolErrorKind.TooShort, ex.Kind);
            }
            // Exactly type + CRC (empty payload) is the minimum valid size.
            byte[] minimal = PacketCodec.WriteRaw((byte)PacketType.Hello, new byte[0]);
            Assert.Equal(3, minimal.Length);
            RawPacket parsed = PacketCodec.ParseRaw(minimal);
            Assert.Equal((byte)PacketType.Hello, parsed.Type);
            Assert.Empty(parsed.Payload);
        }

        [Fact]
        public void WrongLengthKnownPacketsAreRejected()
        {
            // A wrong-length payload for a *known* type is BadLength — the
            // receiver drops it like a CRC failure. One under and one over
            // per fixed-size type; every below-minimum length for HelloAck.
            var cases = new[]
            {
                (PacketType.Hello, 0),
                (PacketType.Hello, 2),
                (PacketType.Frame, 0),
                (PacketType.Frame, PacketCodec.FramePayloadLength - 1),
                (PacketType.Frame, PacketCodec.FramePayloadLength + 1),
                (PacketType.Brightness, 0),
                (PacketType.Brightness, 2),
                (PacketType.HelloAck, 0),
                (PacketType.HelloAck, 1),
                (PacketType.HelloAck, 2),
                (PacketType.ButtonEvent, 0),
                (PacketType.ButtonEvent, 1),
                (PacketType.ButtonEvent, 3),
            };
            foreach (var (type, length) in cases)
            {
                byte[] raw = PacketCodec.WriteRaw((byte)type, new byte[length]);
                var ex = Assert.Throws<ProtocolException>(() => PacketCodec.ParsePacket(raw));
                Assert.True(
                    ex.Kind == ProtocolErrorKind.BadLength,
                    $"{type} with {length}-byte payload: expected BadLength, got {ex.Kind}");
            }
        }

        [Fact]
        public void UnknownTypeSurfacesAsUnknownNotError()
        {
            // Forward compat: a valid-CRC packet with an unassigned type byte
            // parses as UnknownPacket, never an error — receivers skip it.
            byte[] raw = PacketCodec.WriteRaw(0x7E, new byte[] { 0x42 });
            var unknown = Assert.IsType<UnknownPacket>(PacketCodec.ParsePacket(raw));
            Assert.Equal(0x7E, unknown.TypeByte);
            Assert.Equal(new byte[] { 0x42 }, unknown.Payload);
        }

        [Fact]
        public void ButtonEventWithUnassignedIdsStillParses()
        {
            // The 2-byte length is frozen but the id space is open — a future
            // firmware button must not kill old parsers.
            byte[] raw = PacketCodec.WriteRaw((byte)PacketType.ButtonEvent, new byte[] { 7, 9 });
            var parsed = Assert.IsType<ButtonEventPacket>(PacketCodec.ParsePacket(raw));
            Assert.Equal(7, parsed.Button);
            Assert.Equal(9, parsed.Kind);
            Assert.Null(ProtocolIds.ButtonFromByte(7));
            Assert.Null(ProtocolIds.PressKindFromByte(9));
        }

        [Fact]
        public void HelloAckFwVersionOverEncoderCapIsRejected()
        {
            var fw = new byte[PacketCodec.MaxFwVersionLength + 1];
            for (int i = 0; i < fw.Length; i++)
            {
                fw[i] = (byte)'x';
            }
            var packet = new HelloAckPacket(PacketCodec.ProtocolVersion, 32, 32, fw);
            var ex = Assert.Throws<ProtocolException>(() => packet.EncodeWire());
            Assert.Equal(ProtocolErrorKind.BadLength, ex.Kind);
        }

        [Fact]
        public void HelloAckParseAcceptsFwLongerThanEncoderCap()
        {
            // MaxFwVersionLength is encoder-side only; the wire layout has no
            // fw_version length limit.
            var payload = new byte[PacketCodec.HelloAckMinPayloadLength + PacketCodec.MaxFwVersionLength + 5];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)'y';
            }
            payload[0] = PacketCodec.ProtocolVersion;
            payload[1] = 32;
            payload[2] = 32;
            byte[] raw = PacketCodec.WriteRaw((byte)PacketType.HelloAck, payload);
            var parsed = Assert.IsType<HelloAckPacket>(PacketCodec.ParsePacket(raw));
            Assert.Equal(PacketCodec.MaxFwVersionLength + 5, parsed.FwVersion.Length);
        }

        [Fact]
        public void TypedEncodeMatchesLowLevelEncode()
        {
            // Packet.EncodeWire is a thin wrapper over PacketCodec.EncodeWire
            // — the wire bytes must be identical.
            var packet = new HelloAckPacket(1, 32, 32, Encoding.ASCII.GetBytes("1.0"));
            byte[] lowLevel = PacketCodec.EncodeWire(
                (byte)PacketType.HelloAck,
                new byte[] { 1, 32, 32, (byte)'1', (byte)'.', (byte)'0' });
            Assert.Equal(lowLevel, packet.EncodeWire());
        }
    }

    /// <summary>
    /// Golden-vector conformance tests against the frozen files under
    /// <c>testdata/proto/</c>, mirroring the Rust suite in
    /// <c>proto/tests/golden_vectors.rs</c> assertion for assertion.
    /// </summary>
    public class ProtoConformanceTests
    {
        // -----------------------------------------------------------------
        // Frozen expectation tables (the C# mirror of the Rust tables that
        // generate manifest.json — the Rust suite asserts the files match
        // those tables byte-for-byte, so these must agree with the manifest).
        // -----------------------------------------------------------------

        private const byte BrightnessValue = 200;
        private const byte BoundaryTypeByte = 0x7E;
        private static readonly byte[] FwVersion = Encoding.ASCII.GetBytes("2.0.0-test");
        private static readonly byte[] ResyncGarbage = { 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x13, 0x37 };

        public static TheoryData<string> PositiveVectorNames => new TheoryData<string>
        {
            "hello",
            "hello_ack",
            "brightness",
            "button_event_short",
            "button_event_long",
            "frame",
        };

        public static TheoryData<string, ProtocolErrorKind> NegativeVectors =>
            new TheoryData<string, ProtocolErrorKind>
            {
                { "bad_crc", ProtocolErrorKind.BadCrc },
                { "truncated", ProtocolErrorKind.CobsMalformed },
                { "embedded_zero_garbage", ProtocolErrorKind.CobsMalformed },
                { "wrong_length_known_type", ProtocolErrorKind.BadLength },
            };

        /// <summary>
        /// Byte <paramref name="i"/> of the 3072-byte frame payload —
        /// mirrors <c>frame_pixel</c> in the Rust suite (and the prose in the
        /// manifest's <c>frame.purpose</c>).
        /// </summary>
        private static byte FramePixel(int i)
        {
            if (i < 256)
            {
                return i % 8 == 0 ? (byte)0 : (byte)i;
            }
            if (i < 512)
            {
                return 0xFF;
            }
            if (i < 1024)
            {
                return (byte)(i * 7 % 256);
            }
            if (i < 1342)
            {
                return (byte)(i % 253 + 1);
            }
            return (byte)(i % 256);
        }

        private static byte[] FramePixels()
        {
            var pixels = new byte[PacketCodec.FramePayloadLength];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = FramePixel(i);
            }
            return pixels;
        }

        /// <summary>The typed packet each positive vector must parse to.</summary>
        private static Packet ExpectedPacket(string name)
        {
            switch (name)
            {
                case "hello":
                    return new HelloPacket(PacketCodec.ProtocolVersion);
                case "hello_ack":
                    return new HelloAckPacket(
                        PacketCodec.ProtocolVersion,
                        PacketCodec.PanelWidth,
                        PacketCodec.PanelHeight,
                        FwVersion);
                case "brightness":
                    return new BrightnessPacket(BrightnessValue);
                case "button_event_short":
                    return new ButtonEventPacket((byte)Button.BrightnessUp, (byte)PressKind.Short);
                case "button_event_long":
                    return new ButtonEventPacket((byte)Button.Sleep, (byte)PressKind.Long);
                case "frame":
                    return new FramePacket(FramePixels());
                default:
                    throw new ArgumentException($"no expected packet for vector {name}", nameof(name));
            }
        }

        [Theory]
        [MemberData(nameof(PositiveVectorNames))]
        public void RawVectorParsesToTheExpectedTypedPacket(string name)
        {
            byte[] raw = RepoPaths.ReadVector(name + ".raw");
            Packet parsed = PacketCodec.ParsePacket(raw);
            Assert.False(parsed is UnknownPacket, $"{name}: expected a known packet");
            Assert.Equal(ExpectedPacket(name), parsed);
        }

        [Theory]
        [MemberData(nameof(PositiveVectorNames))]
        public void RawVectorReencodesByteIdentically(string name)
        {
            byte[] raw = RepoPaths.ReadVector(name + ".raw");
            AssertBytesEqual($"{name}.raw re-encode", ExpectedPacket(name).EncodeRaw(), raw);
        }

        [Theory]
        [MemberData(nameof(PositiveVectorNames))]
        public void WireVectorDecodesParsesAndReencodesByteIdentically(string name)
        {
            byte[] wire = RepoPaths.ReadVector(name + ".wire");
            byte[] rawFile = RepoPaths.ReadVector(name + ".raw");

            Assert.True(wire[wire.Length - 1] == PacketCodec.Delimiter, $"{name}: missing delimiter");
            int bodyLength = wire.Length - 1;
            for (int i = 0; i < bodyLength; i++)
            {
                Assert.True(wire[i] != 0x00, $"{name}: 0x00 inside the COBS body at offset {i}");
            }

            byte[] decoded = Cobs.Decode(wire, 0, bodyLength);
            AssertBytesEqual($"{name}: decoded wire body", decoded, rawFile);

            Packet expected = ExpectedPacket(name);
            Assert.Equal(expected, PacketCodec.ParsePacket(decoded));
            Assert.Null(Classify(name, wire));

            AssertBytesEqual($"{name}.wire typed re-encode", expected.EncodeWire(), wire);
        }

        /// <summary>
        /// The frame payload actually stresses what it claims to: zero bytes,
        /// a 254+ run of 0xFF, and a 254+ zero-free run — asserted on the
        /// file bytes, not the generator. Also pins the manifest's note that
        /// the payload is <c>raw[1..3073]</c> verbatim.
        /// </summary>
        [Fact]
        public void FrameVectorPayloadStressesCobs()
        {
            byte[] raw = RepoPaths.ReadVector("frame.raw");
            byte[] payload = Slice(raw, 1, raw.Length - 3);
            Assert.Equal(PacketCodec.FramePayloadLength, payload.Length);
            Assert.True(Array.IndexOf(payload, (byte)0x00) >= 0, "frame payload has no zero bytes");
            int ffRun = LongestRun(payload, b => b == 0xFF);
            Assert.True(ffRun >= 254, $"longest 0xFF run is only {ffRun}");
            int nonZeroRun = LongestRun(payload, b => b != 0);
            Assert.True(nonZeroRun >= 254, $"longest zero-free run is only {nonZeroRun}");

            var parsed = Assert.IsType<FramePacket>(PacketCodec.ParsePacket(raw));
            AssertBytesEqual("frame pixels vs raw[1..3073]", parsed.Pixels, payload);
        }

        // The 254-boundary vector: catches Wikipedia-variant COBS encoders.
        [Fact]
        public void CobsBoundary254RawEndsIn254NonZeroBytesAfterAZero()
        {
            byte[] raw = RepoPaths.ReadVector("cobs_boundary_254.raw");

            // Frozen shape: [0x7E][0x51][tweak][0x00][0x01..0xFC][crc16 LE].
            Assert.Equal(258, raw.Length);
            Assert.Equal(BoundaryTypeByte, raw[0]);
            Assert.Equal(0x51, raw[1]);
            for (int j = 0; j < 252; j++)
            {
                Assert.True(raw[4 + j] == (byte)(j + 1), $"ascending run broken at offset {4 + j}");
            }

            int boundary = raw.Length - 255;
            Assert.True(raw[boundary] == 0x00, "no zero before the trailing run");
            for (int i = boundary + 1; i < raw.Length; i++)
            {
                Assert.True(raw[i] != 0, $"the trailing 254 bytes must be zero-free (offset {i})");
            }

            // Valid CRC, unassigned type: parses as Unknown for caller-side
            // ignoring — never an error.
            var unknown = Assert.IsType<UnknownPacket>(PacketCodec.ParsePacket(raw));
            Assert.Equal(BoundaryTypeByte, unknown.TypeByte);
            AssertBytesEqual(
                "cobs_boundary_254 unknown payload",
                unknown.Payload,
                Slice(raw, 1, raw.Length - 3));

            // The CRC our codec computes over type+payload must reproduce the
            // frozen raw bytes exactly.
            AssertBytesEqual("cobs_boundary_254.raw re-encode", unknown.EncodeRaw(), raw);
        }

        [Fact]
        public void CobsBoundary254WireIsTheCanonicalListing1Form()
        {
            byte[] wire = RepoPaths.ReadVector("cobs_boundary_254.wire");
            byte[] rawFile = RepoPaths.ReadVector("cobs_boundary_254.raw");
            int length = wire.Length;

            Assert.True(wire[length - 1] == 0x00, "missing delimiter");
            Assert.True(
                wire[length - 2] == 0x01,
                "canonical trailing group header missing (Wikipedia-variant encoder?)");
            Assert.True(
                wire[length - 257] == 0xFF,
                "expected a full 254-byte group before the trailing header");

            byte[] decoded = Cobs.Decode(wire, 0, length - 1);
            AssertBytesEqual("cobs_boundary_254 decoded wire body", decoded, rawFile);

            // Byte-exact re-encode: this is the assertion a Wikipedia-derived
            // encoder fails (its wire is one byte shorter).
            var unknown = Assert.IsType<UnknownPacket>(PacketCodec.ParsePacket(rawFile));
            AssertBytesEqual("cobs_boundary_254.wire re-encode", unknown.EncodeWire(), wire);

            // The non-canonical (Wikipedia) form — the same bytes minus the
            // trailing 0x01 header — decodes to the identical raw packet.
            // That round-trip blindness is exactly why only the byte-exact
            // comparison above catches a Wikipedia-derived encoder port.
            byte[] decodedShort = Cobs.Decode(wire, 0, length - 2);
            AssertBytesEqual("non-canonical form must decode identically", decodedShort, rawFile);
        }

        [Theory]
        [MemberData(nameof(NegativeVectors))]
        public void NegativeVectorFailsWithExactlyTheExpectedClass(string name, ProtocolErrorKind expected)
        {
            byte[] wire = RepoPaths.ReadVector(name + ".wire");
            Assert.Equal(expected, Classify(name, wire));
        }

        [Fact]
        public void ResyncStreamRecoversExactlyTheEmbeddedPackets()
        {
            byte[] stream = RepoPaths.ReadVector("resync.stream");

            // Layout: garbage (delimiter-free), a lone 0x00, then the
            // on-disk hello.wire and brightness.wire verbatim.
            Assert.True(Array.IndexOf(ResyncGarbage, (byte)0x00) < 0, "garbage must not contain a delimiter");
            byte[] expectedStream = Concat(
                ResyncGarbage,
                new byte[] { 0x00 },
                RepoPaths.ReadVector("hello.wire"),
                RepoPaths.ReadVector("brightness.wire"));
            AssertBytesEqual("resync.stream layout", stream, expectedStream);

            // Walk the stream the way a receiver does: split on 0x00, feed
            // each non-empty segment through COBS + parse, drop failures.
            var recovered = new List<Packet>();
            int start = 0;
            for (int i = 0; i <= stream.Length; i++)
            {
                if (i < stream.Length && stream[i] != PacketCodec.Delimiter)
                {
                    continue;
                }
                int segmentLength = i - start;
                if (segmentLength > 0)
                {
                    try
                    {
                        recovered.Add(PacketCodec.DecodeWire(stream, start, segmentLength));
                    }
                    catch (ProtocolException)
                    {
                        // Drop the segment and resync at the next delimiter.
                    }
                }
                start = i + 1;
            }

            Assert.True(recovered.Count == 2, $"expected exactly two recovered packets, got {recovered.Count}");
            Assert.Equal(new HelloPacket(PacketCodec.ProtocolVersion), recovered[0]);
            Assert.Equal(new BrightnessPacket(BrightnessValue), recovered[1]);
        }

        // -----------------------------------------------------------------
        // Manifest cross-checks (no JSON dependency, same posture as the
        // Rust suite's manifest_references_every_vector_file).
        // -----------------------------------------------------------------

        [Fact]
        public void ManifestReferencesEveryVectorFileAndErrorClass()
        {
            string manifest = Encoding.UTF8.GetString(RepoPaths.ReadVector("manifest.json"));
            string[] files =
            {
                "hello.raw", "hello.wire",
                "hello_ack.raw", "hello_ack.wire",
                "brightness.raw", "brightness.wire",
                "button_event_short.raw", "button_event_short.wire",
                "button_event_long.raw", "button_event_long.wire",
                "frame.raw", "frame.wire",
                "cobs_boundary_254.raw", "cobs_boundary_254.wire",
                "bad_crc.wire", "truncated.wire", "embedded_zero_garbage.wire",
                "wrong_length_known_type.wire", "resync.stream",
            };
            foreach (string file in files)
            {
                Assert.True(manifest.Contains($"\"{file}\""), $"manifest.json does not reference {file}");
            }
            foreach (string errorClass in new[] { "cobs_malformed", "bad_crc", "bad_length" })
            {
                Assert.True(manifest.Contains($"\"{errorClass}\""), $"manifest.json missing error class {errorClass}");
            }
            Assert.Contains($"\"protocol_version\": {PacketCodec.ProtocolVersion}", manifest);
            Assert.Contains("\"expected_packets\": [ \"hello\", \"brightness\" ]", manifest);
        }

        /// <summary>
        /// The receiver-side decode pipeline the negative vectors are defined
        /// against: strip the single trailing delimiter, COBS-decode,
        /// CRC-check, typed-parse. Returns <c>null</c> on success, the error
        /// class otherwise; anything outside the contract classes fails.
        /// </summary>
        private static ProtocolErrorKind? Classify(string name, byte[] wire)
        {
            Assert.True(
                wire.Length > 0 && wire[wire.Length - 1] == PacketCodec.Delimiter,
                $"{name}: missing trailing delimiter");
            try
            {
                PacketCodec.DecodeWire(wire, 0, wire.Length - 1);
                return null;
            }
            catch (ProtocolException ex)
            {
                Assert.True(
                    ex.Kind == ProtocolErrorKind.CobsMalformed
                        || ex.Kind == ProtocolErrorKind.BadCrc
                        || ex.Kind == ProtocolErrorKind.BadLength,
                    $"{name}: {ex.Kind} is outside the cross-language error contract");
                return ex.Kind;
            }
        }

        private static int LongestRun(byte[] bytes, Func<byte, bool> predicate)
        {
            int best = 0;
            int current = 0;
            foreach (byte b in bytes)
            {
                current = predicate(b) ? current + 1 : 0;
                best = Math.Max(best, current);
            }
            return best;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Array.Copy(source, offset, result, 0, count);
            return result;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] part in parts)
            {
                total += part.Length;
            }
            var result = new byte[total];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }

        /// <summary>
        /// Byte-exact comparison with a diff-friendly failure message (offset
        /// of the first difference instead of a multi-kilobyte dump).
        /// </summary>
        private static void AssertBytesEqual(string context, byte[] got, byte[] want)
        {
            if (BytesHelper.SequenceEqual(got, want))
            {
                return;
            }
            int limit = Math.Min(got.Length, want.Length);
            int offset = limit;
            for (int i = 0; i < limit; i++)
            {
                if (got[i] != want[i])
                {
                    offset = i;
                    break;
                }
            }
            string gotByte = offset < got.Length ? $"0x{got[offset]:X2}" : "<end>";
            string wantByte = offset < want.Length ? $"0x{want[offset]:X2}" : "<end>";
            throw new XunitException(
                $"{context}: byte mismatch - got {got.Length} bytes, want {want.Length} bytes, " +
                $"first difference at offset {offset} (got {gotByte}, want {wantByte})");
        }
    }

    /// <summary>Shared byte-array helpers for the protocol tests.</summary>
    internal static class BytesHelper
    {
        public static bool SequenceEqual(byte[] a, byte[] b)
        {
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
}
