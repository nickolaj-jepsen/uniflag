// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The Hello/HelloAck handshake (docs/protocol.md §Handshake), performed on
// every (re)connect. Host obligations implemented here:
//
//  - protocol-version EQUALITY is validated by the host; a mismatch is a
//    refuse-with-message (surfaced as device status), never a silent
//    fallback or best-effort mode;
//  - panel size must be 32x32 — anything else is refused the same way;
//  - device→host packets arriving BEFORE the HelloAck are skipped silently:
//    the device gates ButtonEvent reporting on a completed handshake and
//    clears its TX queue on each Hello, but a press landing inside the
//    ~1.5 s silence window can leave one packet wedged in the in-flight
//    write and deliver it to the next session ahead of the ack.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Uniflag.Protocol;

namespace Uniflag.Device
{
    /// <summary>How one handshake attempt ended.</summary>
    public enum HandshakeOutcome
    {
        /// <summary>HelloAck received and validated — proceed to streaming.</summary>
        Success,

        /// <summary>
        /// HelloAck received but unacceptable (protocol-version or
        /// panel-size mismatch). <b>Never</b> driven anyway — the refusal
        /// message is surfaced to the user.
        /// </summary>
        Refused,

        /// <summary>No HelloAck within the deadline (or cancelled).</summary>
        NoAck,
    }

    /// <summary>
    /// Result of <see cref="DeviceHandshake.Perform"/>. On success the
    /// caller takes over <see cref="Decoder"/> (and any
    /// <see cref="TrailingPackets"/> decoded from bytes that followed the
    /// ack in the same read) so no inbound bytes are lost across the
    /// handshake/streaming boundary.
    /// </summary>
    public sealed class HandshakeResult
    {
        public HandshakeOutcome Outcome { get; }

        /// <summary>Human refusal / timeout text; null on success.</summary>
        public string Message { get; }

        /// <summary>The validated ack; non-null only on success.</summary>
        public HelloAckPacket Ack { get; }

        /// <summary>The live stream decoder; non-null only on success.</summary>
        public PacketStreamDecoder Decoder { get; }

        /// <summary>Packets decoded after the ack from the same read; non-null only on success.</summary>
        public IReadOnlyList<Packet> TrailingPackets { get; }

        private HandshakeResult(
            HandshakeOutcome outcome,
            string message,
            HelloAckPacket ack,
            PacketStreamDecoder decoder,
            IReadOnlyList<Packet> trailingPackets)
        {
            Outcome = outcome;
            Message = message;
            Ack = ack;
            Decoder = decoder;
            TrailingPackets = trailingPackets;
        }

        internal static HandshakeResult Success(
            HelloAckPacket ack, PacketStreamDecoder decoder, IReadOnlyList<Packet> trailing)
        {
            return new HandshakeResult(HandshakeOutcome.Success, null, ack, decoder, trailing);
        }

        internal static HandshakeResult Refused(string message)
        {
            return new HandshakeResult(HandshakeOutcome.Refused, message, null, null, null);
        }

        internal static HandshakeResult NoAck(string message)
        {
            return new HandshakeResult(HandshakeOutcome.NoAck, message, null, null, null);
        }
    }

    /// <summary>
    /// Performs the host side of the handshake over an
    /// <see cref="ISerialConnection"/>. Pure conversation logic — no
    /// threading, no port lifecycle — so it is directly testable against
    /// in-memory fakes.
    /// </summary>
    public static class DeviceHandshake
    {
        /// <summary>
        /// Send Hello, wait for a HelloAck, validate it. May throw the
        /// connection's I/O exceptions (yank mid-handshake) — callers treat
        /// that like any other lost link.
        /// </summary>
        /// <param name="connection">The open link; not disposed here.</param>
        /// <param name="timeoutMs">Deadline for the HelloAck.</param>
        /// <param name="cancelled">Checked between reads; true aborts with <see cref="HandshakeOutcome.NoAck"/>.</param>
        public static HandshakeResult Perform(ISerialConnection connection, int timeoutMs, Func<bool> cancelled)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            byte[] hello = new HelloPacket(PacketCodec.ProtocolVersion).EncodeWire();
            connection.Write(hello, 0, hello.Length);

            var decoder = new PacketStreamDecoder();
            var buffer = new byte[512];
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMs && !(cancelled != null && cancelled()))
            {
                int n = connection.Read(buffer, 0, buffer.Length);
                if (n <= 0)
                {
                    continue;
                }
                List<Packet> packets = decoder.Feed(buffer, 0, n);
                for (int i = 0; i < packets.Count; i++)
                {
                    if (!(packets[i] is HelloAckPacket ack))
                    {
                        // Pre-ack device→host packet (e.g. a ButtonEvent
                        // wedged across sessions) or line noise the decoder
                        // already survived: skip silently, per protocol.md.
                        continue;
                    }
                    return Validate(ack, decoder, packets, i + 1);
                }
            }
            return HandshakeResult.NoAck(
                $"No HelloAck within {timeoutMs} ms — is v2 firmware flashed and is this the right port?");
        }

        private static HandshakeResult Validate(
            HelloAckPacket ack, PacketStreamDecoder decoder, List<Packet> packets, int trailingFrom)
        {
            if (ack.ProtocolVersion != PacketCodec.ProtocolVersion)
            {
                return HandshakeResult.Refused(
                    $"Protocol version mismatch: plugin v{PacketCodec.ProtocolVersion}, device "
                    + $"v{ack.ProtocolVersion} — update the firmware or the plugin so they match.");
            }
            if (ack.Width != PacketCodec.PanelWidth || ack.Height != PacketCodec.PanelHeight)
            {
                return HandshakeResult.Refused(
                    $"Unsupported panel size {ack.Width}x{ack.Height} — this plugin drives a "
                    + $"{PacketCodec.PanelWidth}x{PacketCodec.PanelHeight} Cosmic Unicorn.");
            }
            // Post-ack packets in the same read chunk (e.g. ButtonEvents)
            // handed to the caller so they are not lost.
            var trailing = new List<Packet>();
            for (int i = trailingFrom; i < packets.Count; i++)
            {
                trailing.Add(packets[i]);
            }
            return HandshakeResult.Success(ack, decoder, trailing);
        }
    }
}
