// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Shared in-memory fakes for the M9 device tests: an ISerialConnection
// duplex fake with scripted RX bytes, captured TX bytes and failure/stall
// injection; a scripted connection factory; a fake port enumeration; and a
// registration-counting sink host. No test in this suite ever opens a real
// serial port — CI-safe by construction.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Uniflag.Device;
using Uniflag.Protocol;
using Uniflag.Rendering;
using Uniflag.Web;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// In-memory duplex stand-in for one serial link. RX side: tests
    /// enqueue byte chunks the "device" sends; <see cref="Read"/> honours
    /// the bounded-block contract (short wait, 0 on no data). TX side:
    /// every write is captured verbatim. Failure injection: reads/writes
    /// can be made to throw, and <see cref="WriteGate"/> stalls writes to
    /// exercise the never-block-the-renderer path.
    /// </summary>
    internal sealed class FakeConnection : ISerialConnection
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _rx = new Queue<byte[]>();
        private readonly MemoryStream _tx = new MemoryStream();
        private readonly ManualResetEventSlim _rxAvailable = new ManualResetEventSlim(false);
        private int _rxChunkOffset;
        private volatile bool _failReads;
        private volatile bool _failWrites;
        private volatile bool _disposed;
        private int _writeAttempts;

        internal FakeConnection(string portName = "COM7")
        {
            PortName = portName;
        }

        /// <summary>
        /// When non-null, every write blocks on this gate first (up to 5 s,
        /// then faults) — the stalled-writer injection. Set the event to let
        /// writes flow.
        /// </summary>
        internal ManualResetEventSlim WriteGate { get; set; }

        public string PortName { get; }

        internal bool Disposed => _disposed;

        /// <summary>Writes started (counted before any stall/fault).</summary>
        internal int WriteAttempts => Volatile.Read(ref _writeAttempts);

        /// <summary>Everything the host has written, in order.</summary>
        internal byte[] TxBytes
        {
            get
            {
                lock (_gate)
                {
                    return _tx.ToArray();
                }
            }
        }

        internal void EnqueueRx(byte[] bytes)
        {
            lock (_gate)
            {
                _rx.Enqueue(bytes);
            }
            _rxAvailable.Set();
        }

        /// <summary>Subsequent reads throw (simulated yank on the RX path).</summary>
        internal void FailReads()
        {
            _failReads = true;
            _rxAvailable.Set();
        }

        /// <summary>Subsequent writes throw (simulated yank on the TX path).</summary>
        internal void FailWrites()
        {
            _failWrites = true;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_failReads || _disposed)
            {
                throw new IOException("simulated read failure");
            }
            if (!_rxAvailable.Wait(5))
            {
                return 0; // bounded-block contract: quiet link
            }
            if (_failReads || _disposed)
            {
                throw new IOException("simulated read failure");
            }
            lock (_gate)
            {
                if (_rx.Count == 0)
                {
                    _rxAvailable.Reset();
                    return 0;
                }
                byte[] chunk = _rx.Peek();
                int available = chunk.Length - _rxChunkOffset;
                int n = Math.Min(available, count);
                Buffer.BlockCopy(chunk, _rxChunkOffset, buffer, offset, n);
                _rxChunkOffset += n;
                if (_rxChunkOffset == chunk.Length)
                {
                    _rx.Dequeue();
                    _rxChunkOffset = 0;
                    if (_rx.Count == 0)
                    {
                        _rxAvailable.Reset();
                    }
                }
                return n;
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _writeAttempts);
            ManualResetEventSlim stall = WriteGate;
            if (stall != null && !stall.Wait(5000))
            {
                // Mirrors SerialPort's WriteTimeout posture: a link that
                // refuses progress faults rather than hanging forever.
                throw new IOException("simulated write timeout");
            }
            if (_failWrites || _disposed)
            {
                throw new IOException("simulated write failure");
            }
            lock (_gate)
            {
                _tx.Write(buffer, offset, count);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _rxAvailable.Set(); // fault any blocked read promptly
        }
    }

    /// <summary>
    /// Scripted <see cref="ISerialConnectionFactory"/>: each Open consumes
    /// the next scripted item (a connection, or a failure). An empty script
    /// throws — the manager treats it like any open failure and keeps
    /// scanning.
    /// </summary>
    internal sealed class FakeConnectionFactory : ISerialConnectionFactory
    {
        private readonly object _gate = new object();
        private readonly Queue<object> _script = new Queue<object>();
        private readonly List<string> _openedPorts = new List<string>();

        internal int OpenAttempts
        {
            get
            {
                lock (_gate)
                {
                    return _openedPorts.Count;
                }
            }
        }

        internal IReadOnlyList<string> OpenedPorts
        {
            get
            {
                lock (_gate)
                {
                    return _openedPorts.ToArray();
                }
            }
        }

        internal void EnqueueConnection(FakeConnection connection)
        {
            lock (_gate)
            {
                _script.Enqueue(connection);
            }
        }

        internal void EnqueueFailure(string message)
        {
            lock (_gate)
            {
                _script.Enqueue(message);
            }
        }

        public ISerialConnection Open(string portName)
        {
            lock (_gate)
            {
                _openedPorts.Add(portName);
                if (_script.Count == 0)
                {
                    throw new IOException("no scripted connection left");
                }
                object next = _script.Dequeue();
                if (next is string failure)
                {
                    // Fresh exception per attempt, like a real open failure
                    // (UnauthorizedAccessException while Windows holds the
                    // stale handle after a replug).
                    throw new UnauthorizedAccessException(failure);
                }
                return (ISerialConnection)next;
            }
        }
    }

    /// <summary>Injected port enumeration for the discovery scan.</summary>
    internal sealed class FakePortEnumerator : IPortEnumerator
    {
        private volatile SerialPortInfo[] _ports = Array.Empty<SerialPortInfo>();
        private int _scans;

        internal int Scans => Volatile.Read(ref _scans);

        internal void SetPorts(params SerialPortInfo[] ports)
        {
            _ports = ports;
        }

        public IReadOnlyList<SerialPortInfo> EnumeratePorts()
        {
            Interlocked.Increment(ref _scans);
            return _ports;
        }
    }

    /// <summary>
    /// Registration-counting stand-in for the renderer (same shape as the
    /// M5 web-server tests): the manager only needs AddSink/RemoveSink, so
    /// tests observe the refcount without spinning up a render thread.
    /// </summary>
    internal sealed class CountingSinkHost : IFrameSinkHost
    {
        private int _adds;
        private int _removes;

        internal int Adds => Volatile.Read(ref _adds);

        internal int Removes => Volatile.Read(ref _removes);

        public void AddSink(IFrameSink sink)
        {
            Interlocked.Increment(ref _adds);
        }

        public void RemoveSink(IFrameSink sink)
        {
            Interlocked.Increment(ref _removes);
        }
    }

    /// <summary>Shared helpers for the device test suite.</summary>
    internal static class DeviceTestUtil
    {
        internal static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"timed out waiting for {what}");
                Thread.Sleep(5);
            }
        }

        /// <summary>A valid HelloAck wire packet (defaults match the real panel).</summary>
        internal static byte[] HelloAckWire(
            byte protocolVersion = PacketCodec.ProtocolVersion,
            byte width = 32,
            byte height = 32,
            string fwVersion = "0.2.0")
        {
            return new HelloAckPacket(
                protocolVersion, width, height,
                System.Text.Encoding.ASCII.GetBytes(fwVersion)).EncodeWire();
        }

        internal static byte[] ButtonEventWire(byte button, byte kind)
        {
            return new ButtonEventPacket(button, kind).EncodeWire();
        }

        /// <summary>Decode a captured TX byte stream into typed packets.</summary>
        internal static List<Packet> DecodeStream(byte[] bytes)
        {
            return new PacketStreamDecoder().Feed(bytes, 0, bytes.Length);
        }
    }
}
