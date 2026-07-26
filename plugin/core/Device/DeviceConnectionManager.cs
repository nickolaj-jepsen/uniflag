// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The USB device connection manager: a background worker that owns the
// serial port exclusively and walks
//
//   scan → open (retry-on-open with backoff) → handshake → Brightness →
//   streaming → (yank/fault) → back to scan
//
// per docs/protocol.md §Handshake. It doubles as the USB frame sink: it
// registers itself with the renderer (via IFrameSinkHost) while — and only
// while — the streaming state holds, so the renderer runs whenever the panel
// is attached and the connected-idle marker streams with no game running.
//
// Threading: all device I/O happens on two dedicated background threads —
// the connection worker (lifecycle + TX pump) and a per-session RX pump.
// The render thread only ever touches the depth-one frame slot in OnFrame
// (one bounded copy behind a short lock, mirroring OverlayClient.Post); the
// SimHub update thread never enters this class; the UI thread only reads
// the immutable status snapshot and pokes the brightness/override inputs.
// Stop joins both workers and releases the COM port — SimHub rebuilds
// plugins (End then Init) at every game change, so teardown must be
// complete, prompt, and idempotent.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Protocol;
using Uniflag.Rendering;

namespace Uniflag.Device
{
    /// <summary>
    /// Owns the physical panel connection end-to-end: discovery, the
    /// handshake, the 30 fps frame stream (heartbeat — no acks), inbound
    /// ButtonEvents into the <see cref="BrightnessPolicy"/>, and reconnect
    /// after yanks. Exposes an immutable <see cref="Status"/> snapshot for
    /// the settings tab.
    /// </summary>
    public sealed class DeviceConnectionManager : IFrameSink, IDisposable
    {
        private readonly IFrameSinkHost _host;
        private readonly IPortEnumerator _enumerator;
        private readonly ISerialConnectionFactory _factory;
        private readonly BrightnessPolicy _brightness;
        private readonly DeviceConnectionOptions _options;

        // Lifecycle. _kick interrupts scan/backoff sleeps (Stop, override
        // change); _stopRequested is checked by every loop.
        private readonly object _lifecycleGate = new object();
        private readonly AutoResetEvent _kick = new AutoResetEvent(false);
        private Thread _worker;
        private volatile bool _stopRequested;
        private bool _disposed;

        // TX slots: depth-one, newest-wins — a wedged write can never back
        // up the renderer; stale frames are dropped, never queued (the
        // OverlayClient posture). _txWake is capped at 1: posts while a
        // send is in flight coalesce onto the already-signalled wake-up.
        private readonly object _txGate = new object();
        private readonly byte[] _pendingFrame = new byte[PacketCodec.FramePayloadLength];
        private bool _hasPendingFrame;
        private byte? _pendingBrightness;
        private readonly SemaphoreSlim _txWake = new SemaphoreSlim(0, 1);

        // TX-pump-only scratch: the pending frame is copied here under
        // _txGate, then encoded and written outside the lock.
        private readonly byte[] _txFramePixels = new byte[PacketCodec.FramePayloadLength];

        // Brightness rate limit (Stopwatch ticks). 0 = immediately eligible;
        // reset at every session start so the (re)connect send is never
        // delayed by a stale timestamp.
        private long _brightnessDueAtTicks;

        // Session flags. _streaming gates OnFrame; _sessionFault (first
        // exception message wins) and _reconnectRequested end the session
        // loops cooperatively.
        private volatile bool _streaming;
        private volatile string _sessionFault;
        private volatile bool _reconnectRequested;

        // Manual override (null = auto-discovery) and the published status.
        private string _manualPortOverride;
        private DeviceStatus _status =
            new DeviceStatus(DeviceConnectionState.Stopped, null, null, null, null, null, null);

        /// <param name="host">Renderer sink registration seam.</param>
        /// <param name="enumerator">Platform port enumeration (scan loop only).</param>
        /// <param name="factory">Serial connection factory.</param>
        /// <param name="brightness">The shared brightness policy; its changes are forwarded as Brightness packets.</param>
        /// <param name="options">Timing knobs; tests shrink them.</param>
        public DeviceConnectionManager(
            IFrameSinkHost host,
            IPortEnumerator enumerator,
            ISerialConnectionFactory factory,
            BrightnessPolicy brightness,
            DeviceConnectionOptions options)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _brightness = brightness ?? throw new ArgumentNullException(nameof(brightness));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _brightness.Changed += OnBrightnessChanged;
        }

        /// <summary>Current connection snapshot for the settings tab. Never null.</summary>
        public DeviceStatus Status => Volatile.Read(ref _status);

        /// <summary>
        /// Manual COM-port override; null or whitespace means automatic
        /// VID/PID discovery. Setting it kicks the scan loop, and a session
        /// on a different port — whether already streaming or still inside
        /// the connect/handshake window when the override lands — reconciles
        /// against it in the TX pump and requests a clean reconnect, so the
        /// override applies promptly rather than at the next unplug.
        /// </summary>
        public string ManualPortOverride
        {
            get => Volatile.Read(ref _manualPortOverride);
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                Volatile.Write(ref _manualPortOverride, normalized);
                // Wake the TX pump (an active session reconciles its port
                // against the override in TxLoop and unwinds on a mismatch)
                // and the scan loop (a parked scan retries immediately).
                ReleaseTxWake();
                _kick.Set();
            }
        }

        /// <summary>Start the background worker. Idempotent; no-op after dispose.</summary>
        public void Start()
        {
            lock (_lifecycleGate)
            {
                if (_worker != null || _disposed)
                {
                    return;
                }
                _stopRequested = false;
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "uniflag-device",
                };
                _worker.Start();
            }
        }

        /// <summary>
        /// Stop the worker, close the port, and join (bounded). Idempotent
        /// and re-Init-safe: after this returns the COM port is released and
        /// a fresh manager (SimHub re-Init) can claim it.
        /// </summary>
        public void Stop()
        {
            Thread worker;
            lock (_lifecycleGate)
            {
                worker = _worker;
                _worker = null;
                if (worker == null)
                {
                    return;
                }
                _stopRequested = true;
            }
            _kick.Set();
            ReleaseTxWake();
            // Bounded: the RX pump's Read returns within its poll interval,
            // the TX pump's Write faults within the connection's write
            // timeout, and every sleep is _kick-interruptible.
            worker.Join(10000);
            PublishStatus(new DeviceStatus(DeviceConnectionState.Stopped, null, null, null, null, null, null));
        }

        /// <summary>Stop and detach from the brightness policy.</summary>
        public void Dispose()
        {
            Stop();
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }
            _brightness.Changed -= OnBrightnessChanged;
        }

        // IFrameSink — called on the render thread at 60 fps.

        /// <summary>
        /// <see cref="IFrameSink"/> entry point. Decimated to 30 fps by
        /// forwarding even tick indices only — parity sampling locked to the
        /// renderer clock, no second timer to drift against, and honest
        /// under skipped ticks (a skipped even tick is simply absent, never
        /// substituted). Never blocks: one bounded buffer copy behind a short lock.
        /// </summary>
        public void OnFrame(byte[] rgb888, long frameIndex)
        {
            if ((frameIndex & 1L) != 0L)
            {
                return;
            }
            if (!_streaming)
            {
                return; // no session — don't overwrite the slot pointlessly
            }
            lock (_txGate)
            {
                Buffer.BlockCopy(rgb888, 0, _pendingFrame, 0, PacketCodec.FramePayloadLength);
                _hasPendingFrame = true;
            }
            ReleaseTxWake();
        }

        // Connection worker.

        private void WorkerLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    RunOnePass();
                }
                catch (Exception ex)
                {
                    // Defensive: an unhandled exception on a background
                    // thread would take SimHub down. Record and keep going.
                    PublishScanning("device worker error: " + ex.Message);
                    WaitKick(_options.ScanIntervalMs);
                }
            }
        }

        /// <summary>One scan → connect → stream pass; returns to rescan.</summary>
        private void RunOnePass()
        {
            string candidate = FindCandidate();
            if (candidate == null)
            {
                WaitKick(_options.ScanIntervalMs);
                return;
            }

            PublishConnecting(candidate);
            ISerialConnection connection = OpenWithRetry(candidate);
            if (connection == null)
            {
                WaitKick(_options.ScanIntervalMs);
                return;
            }

            try
            {
                HandshakeResult result;
                try
                {
                    result = DeviceHandshake.Perform(
                        connection, _options.HandshakeTimeoutMs, () => _stopRequested);
                }
                catch (Exception ex)
                {
                    // Yanked mid-handshake: back to scanning.
                    PublishScanning(candidate + ": " + ex.Message);
                    WaitKick(_options.ScanIntervalMs);
                    return;
                }
                if (_stopRequested)
                {
                    return;
                }

                switch (result.Outcome)
                {
                    case HandshakeOutcome.Refused:
                        // Refuse-with-message: never drive the device, park
                        // the message in the status, retry slowly.
                        PublishRefused(candidate, result.Message);
                        WaitKick(_options.RefusedRetryMs);
                        return;
                    case HandshakeOutcome.NoAck:
                        PublishScanning(candidate + ": " + result.Message);
                        WaitKick(_options.ScanIntervalMs);
                        return;
                    default:
                        StreamSession(connection, result, candidate);
                        if (!_stopRequested)
                        {
                            PublishScanning(_sessionFault);
                        }
                        return;
                }
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>
        /// Next port to try. A manual override bypasses both the native
        /// enumeration and the VID/PID filter (handshake still validates);
        /// otherwise scan and filter. Enumeration failures count as "no
        /// candidate this pass" and surface in the status.
        /// </summary>
        private string FindCandidate()
        {
            string overridePort = Volatile.Read(ref _manualPortOverride);
            if (overridePort != null)
            {
                return DeviceDiscovery.SelectCandidate(null, overridePort);
            }
            try
            {
                string candidate = DeviceDiscovery.SelectCandidate(_enumerator.EnumeratePorts(), null);
                if (candidate == null)
                {
                    PublishScanning(null);
                }
                return candidate;
            }
            catch (Exception ex)
            {
                PublishScanning("port enumeration failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Open with the configured backoff schedule. Windows holds the
        /// stale COM handle briefly after a replug, so early attempts often
        /// throw access-denied — that is expected, not fatal.
        /// </summary>
        private ISerialConnection OpenWithRetry(string portName)
        {
            Exception last = null;
            int[] backoff = _options.OpenRetryBackoffMs;
            for (int attempt = 0; attempt <= backoff.Length; attempt++)
            {
                if (_stopRequested)
                {
                    return null;
                }
                try
                {
                    return _factory.Open(portName);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
                if (attempt < backoff.Length && WaitKick(backoff[attempt]))
                {
                    return null;
                }
            }
            PublishScanning("could not open " + portName + ": " + last?.Message);
            return null;
        }

        // Streaming session.

        /// <summary>
        /// One streaming session: reset the TX slots (queue the persisted
        /// brightness — re-sent on every (re)connect, the device never
        /// assumes a value survives), register as a renderer sink, pump TX
        /// on this thread and RX on a helper, unwind on fault/stop/
        /// reconnect-request.
        /// </summary>
        private void StreamSession(ISerialConnection connection, HandshakeResult handshake, string portName)
        {
            _sessionFault = null;
            _reconnectRequested = false;
            // Read outside _txGate: the policy raises Changed while holding
            // its own lock and the handler takes _txGate, so the lock order
            // is policy-gate → _txGate — never nest the other way around.
            byte connectBrightness = _brightness.Current;
            lock (_txGate)
            {
                _hasPendingFrame = false; // a stale frame from a previous session must not leak
                _pendingBrightness = connectBrightness;
            }
            Interlocked.Exchange(ref _brightnessDueAtTicks, 0);
            ReleaseTxWake();

            _streaming = true;
            _host.AddSink(this);
            // Published only after the sink is live: a Streaming status
            // always implies frames are being accepted and delivered.
            PublishStreaming(portName, handshake.Ack);
            var rx = new Thread(() => RxLoop(connection, handshake.Decoder, handshake.TrailingPackets))
            {
                IsBackground = true,
                Name = "uniflag-device-rx",
            };
            rx.Start();
            try
            {
                TxLoop(connection, portName);
            }
            finally
            {
                _streaming = false;
                _host.RemoveSink(this);
                // Dispose before joining: the RX pump may sit inside a
                // bounded Read — closing the port faults it promptly.
                connection.Dispose();
                rx.Join(5000);
            }
        }

        /// <summary>
        /// The TX pump: waits for a wake (or the idle poll), drains the
        /// depth-one brightness and frame slots, writes. Any write failure
        /// is a lost link — record and unwind into a reconnect. Also the
        /// session's override reconciliation point: an override naming a
        /// different port unwinds the session no matter when it landed.
        /// </summary>
        private void TxLoop(ISerialConnection connection, string portName)
        {
            while (!_stopRequested && _sessionFault == null && !_reconnectRequested)
            {
                // The idle poll bounds how long a rate-limited brightness
                // value or a stop request can go unnoticed with no frames.
                _txWake.Wait(_options.TxIdlePollMs);

                // Reconcile against the manual override every cycle. The
                // worker picks its candidate once per pass, so an override
                // applied after that read — including during the multi-
                // second open-retry/handshake window, when there is no
                // Streaming status for the setter to key off — must be
                // honoured here or the session would stream on the stale
                // port until the next unplug.
                string overridePort = Volatile.Read(ref _manualPortOverride);
                if (overridePort != null
                    && !string.Equals(portName, overridePort, StringComparison.OrdinalIgnoreCase))
                {
                    _reconnectRequested = true;
                    return;
                }

                byte? brightness = null;
                bool hasFrame;
                lock (_txGate)
                {
                    if (_pendingBrightness.HasValue && BrightnessSendDue())
                    {
                        brightness = _pendingBrightness;
                        _pendingBrightness = null;
                    }
                    hasFrame = _hasPendingFrame;
                    if (hasFrame)
                    {
                        Buffer.BlockCopy(_pendingFrame, 0, _txFramePixels, 0, PacketCodec.FramePayloadLength);
                        _hasPendingFrame = false;
                    }
                }

                try
                {
                    if (brightness.HasValue)
                    {
                        byte[] wire = new BrightnessPacket(brightness.Value).EncodeWire();
                        connection.Write(wire, 0, wire.Length);
                        Interlocked.Exchange(
                            ref _brightnessDueAtTicks,
                            Stopwatch.GetTimestamp()
                                + _options.MinBrightnessIntervalMs * (Stopwatch.Frequency / 1000));
                    }
                    if (hasFrame)
                    {
                        byte[] wire = PacketCodec.EncodeWire((byte)PacketType.Frame, _txFramePixels);
                        connection.Write(wire, 0, wire.Length);
                    }
                }
                catch (Exception ex)
                {
                    RecordSessionFault(ex.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// Whether the brightness rate limit has elapsed. The pending slot
        /// keeps the newest value while ineligible, and the idle poll (or
        /// the 30 fps frame wake-ups) retries — the trailing value always
        /// lands.
        /// </summary>
        private bool BrightnessSendDue()
        {
            return Stopwatch.GetTimestamp() >= Interlocked.Read(ref _brightnessDueAtTicks);
        }

        /// <summary>
        /// The RX pump: continuously decodes inbound bytes (resync on 0x00,
        /// bad segments dropped by the decoder). ButtonEvents feed the
        /// brightness policy; a mid-stream HelloAck (device rebooted — the
        /// port dies on its own moments later) and unknown packet types are
        /// ignored (forward compat). A read failure is a lost link.
        /// </summary>
        private void RxLoop(
            ISerialConnection connection, PacketStreamDecoder decoder, IReadOnlyList<Packet> initial)
        {
            if (initial != null)
            {
                for (int i = 0; i < initial.Count; i++)
                {
                    DispatchInbound(initial[i]);
                }
            }
            var buffer = new byte[4096];
            while (!_stopRequested && _sessionFault == null && !_reconnectRequested)
            {
                int n;
                try
                {
                    n = connection.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    RecordSessionFault(ex.Message);
                    return;
                }
                if (n <= 0)
                {
                    continue;
                }
                foreach (Packet packet in decoder.Feed(buffer, 0, n))
                {
                    DispatchInbound(packet);
                }
            }
        }

        private void DispatchInbound(Packet packet)
        {
            if (packet is ButtonEventPacket buttonEvent)
            {
                // Long presses and unassigned ids are filtered inside the
                // policy; its Changed event loops back into the TX slot via
                // OnBrightnessChanged.
                _brightness.ApplyButtonEvent(buttonEvent.Button, buttonEvent.Kind);
            }
        }

        /// <summary>
        /// Policy change (slider or device button): queue exactly one
        /// Brightness send. The depth-one slot coalesces rapid changes —
        /// a dragging slider never floods the wire.
        /// </summary>
        private void OnBrightnessChanged(byte value)
        {
            lock (_txGate)
            {
                _pendingBrightness = value;
            }
            ReleaseTxWake();
        }

        private void RecordSessionFault(string message)
        {
            // First fault wins; both pumps observe it and unwind.
            if (_sessionFault == null)
            {
                _sessionFault = message;
            }
            ReleaseTxWake();
        }

        // Plumbing.

        private void ReleaseTxWake()
        {
            try
            {
                _txWake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled — the pending wake covers this post.
            }
        }

        /// <summary>Interruptible sleep; true when stopping.</summary>
        private bool WaitKick(int milliseconds)
        {
            _kick.WaitOne(milliseconds);
            return _stopRequested;
        }

        private void PublishStatus(DeviceStatus status)
        {
            Volatile.Write(ref _status, status);
        }

        private void PublishScanning(string lastError)
        {
            PublishStatus(new DeviceStatus(
                DeviceConnectionState.Scanning, null, null, null, null, null, lastError));
        }

        private void PublishConnecting(string portName)
        {
            PublishStatus(new DeviceStatus(
                DeviceConnectionState.Connecting, portName, null, null, null, null, null));
        }

        private void PublishRefused(string portName, string message)
        {
            PublishStatus(new DeviceStatus(
                DeviceConnectionState.Refused, portName, null, null, null, null, message));
        }

        private void PublishStreaming(string portName, HelloAckPacket ack)
        {
            PublishStatus(new DeviceStatus(
                DeviceConnectionState.Streaming,
                portName,
                ack.FwVersionString,
                ack.ProtocolVersion,
                ack.Width,
                ack.Height,
                null));
        }
    }
}
