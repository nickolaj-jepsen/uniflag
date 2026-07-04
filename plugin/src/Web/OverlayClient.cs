// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// One upgraded WebSocket connection of the overlay server: RFC 6455 framing
// over a raw TcpClient. Hand-rolled because net48 has no server-side
// WebSocket outside HttpListener, and the server deliberately avoids
// http.sys (see OverlayWebServer). The send side is a depth-one
// newest-frame-wins queue — a flag display only cares about the latest
// frame — and a socket that refuses progress beyond SendTimeoutMs is
// disconnected so it can never stall the render thread or its siblings.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Uniflag.Rendering;

namespace Uniflag.Web
{
    internal sealed class OverlayClient
    {
        // Disconnect bound for a socket that refuses progress. The kernel
        // send buffer absorbs ~20 frames, so a healthy-but-throttled tab
        // (background rAF throttling never stops the TCP reader) stays far
        // from this; only a peer that stopped reading altogether hits it.
        private const int SendTimeoutMs = 2000;

        // Inbound data frames are page->plugin traffic the protocol doesn't
        // define: drained and ignored. A peer streaming larger messages is
        // broken — cut it off rather than buffer.
        private const int MaxInboundPayload = 64 * 1024;

        // FIN|binary opcode, unmasked, 16-bit extended length.
        private const int WireHeaderLength = 4;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Action<OverlayClient> _onGone;
        private readonly CancellationTokenSource _cts;

        // Depth-one outbound queue: _pending always holds the newest posted
        // frame. Frames posted while a send is in flight overwrite it and
        // ride the already-signalled wake-up (newest-frame coalescing —
        // stale frames are dropped, never queued).
        private readonly object _gate = new object();
        private readonly byte[] _pending = new byte[FrameBuffer.ByteLength];
        private bool _hasPending;
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);

        // The frame sender and control-frame echoes (pong, close) share the
        // stream: one writer at a time or the framing corrupts.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Reused wire buffer for the fixed-size frame broadcast.
        private readonly byte[] _wire;

        private int _gone;

        internal OverlayClient(
            TcpClient tcp, NetworkStream stream, Action<OverlayClient> onGone, CancellationToken serverCt)
        {
            _tcp = tcp;
            _stream = stream;
            _onGone = onGone;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            _wire = new byte[WireHeaderLength + FrameBuffer.ByteLength];
            _wire[0] = 0x82; // FIN | binary
            _wire[1] = 126;  // 16-bit extended payload length follows
            _wire[2] = (byte)(FrameBuffer.ByteLength >> 8);
            _wire[3] = (byte)(FrameBuffer.ByteLength & 0xFF);
        }

        /// <summary>
        /// Queue the newest frame. Called on the render thread: never blocks
        /// beyond one bounded buffer copy behind a short lock, regardless of
        /// what the socket is doing.
        /// </summary>
        internal void Post(byte[] rgb888)
        {
            lock (_gate)
            {
                Buffer.BlockCopy(rgb888, 0, _pending, 0, FrameBuffer.ByteLength);
                if (_hasPending)
                {
                    return; // coalesce: the queued wake-up sends this frame instead
                }
                _hasPending = true;
            }
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // _hasPending makes this unreachable; guarded so the render
                // thread can never be taken down from here.
            }
        }

        /// <summary>
        /// Run both pumps until the connection dies. Never throws; the
        /// server awaits this to know when the connection is fully drained.
        /// </summary>
        internal async Task RunAsync()
        {
            Task receive = ReceiveLoopAsync();
            Task send = SendLoopAsync();
            await Task.WhenAll(receive, send).ConfigureAwait(false);
            // Dispose unhooks the linked-token registration so a long-lived
            // server doesn't accumulate one per connection ever served.
            _cts.Dispose();
        }

        /// <summary>
        /// Idempotent teardown: close the socket (which faults any pending
        /// read/write and lets the OS drain queued data in the background),
        /// cancel both pumps, and notify the server exactly once.
        /// </summary>
        internal void Drop()
        {
            if (Interlocked.Exchange(ref _gone, 1) != 0)
            {
                return;
            }
            try { _tcp.Close(); } catch { }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _onGone(this);
        }

        // ---------------------------------------------------------------------
        // Send side
        // ---------------------------------------------------------------------

        private async Task SendLoopAsync()
        {
            CancellationToken ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        Buffer.BlockCopy(_pending, 0, _wire, WireHeaderLength, FrameBuffer.ByteLength);
                        _hasPending = false;
                    }
                    if (!await WriteBoundedAsync(_wire, 0, _wire.Length, ct).ConfigureAwait(false))
                    {
                        break; // stalled beyond the bound; WriteBoundedAsync dropped us
                    }
                }
            }
            catch
            {
                // Cancelled or the socket died under the write — this client is done.
            }
            finally
            {
                Drop();
            }
        }

        /// <summary>
        /// Write one complete wire frame, serialized against other writers.
        /// Returns false (after aborting the connection) if the peer refuses
        /// progress for <see cref="SendTimeoutMs"/> — net48 socket writes
        /// ignore cancellation once started, so closing the socket is the
        /// only reliable unblock for a stalled send.
        /// </summary>
        private async Task<bool> WriteBoundedAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Task write = _stream.WriteAsync(buffer, offset, count, CancellationToken.None);
                if (!write.IsCompleted)
                {
                    Task first = await Task.WhenAny(write, Task.Delay(SendTimeoutMs, ct)).ConfigureAwait(false);
                    if (first != write)
                    {
                        DetachedTask.Observe(write);
                        Drop(); // closing the socket faults the pending write
                        return false;
                    }
                }
                await write.ConfigureAwait(false); // propagate socket faults
                return true;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ---------------------------------------------------------------------
        // Receive side
        // ---------------------------------------------------------------------

        /// <summary>
        /// Frame pump for client->server traffic: processes the close
        /// handshake and pings promptly (so a closed tab is noticed without
        /// waiting for a send failure), drains and ignores everything else.
        /// Reads are unbounded by design — a silent client is a healthy
        /// client; <see cref="Drop"/> unblocks the pending read by closing
        /// the socket.
        /// </summary>
        private async Task ReceiveLoopAsync()
        {
            byte[] header = new byte[10];
            byte[] mask = new byte[4];
            byte[] control = new byte[125];
            byte[] scratch = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(header, 0, 2).ConfigureAwait(false))
                    {
                        break;
                    }
                    int opcode = header[0] & 0x0F;
                    bool masked = (header[1] & 0x80) != 0;
                    long length = header[1] & 0x7F;
                    if (length == 126)
                    {
                        if (!await ReadExactAsync(header, 2, 2).ConfigureAwait(false))
                        {
                            break;
                        }
                        length = (header[2] << 8) | header[3];
                    }
                    else if (length == 127)
                    {
                        if (!await ReadExactAsync(header, 2, 8).ConfigureAwait(false))
                        {
                            break;
                        }
                        // Anything with the top bytes set is far beyond the
                        // inbound cap anyway; avoid the 64-bit arithmetic.
                        if (header[2] != 0 || header[3] != 0 || header[4] != 0 || header[5] != 0)
                        {
                            break;
                        }
                        length = ((long)header[6] << 24) | ((long)header[7] << 16)
                            | ((long)header[8] << 8) | header[9];
                    }
                    if (length > MaxInboundPayload)
                    {
                        break; // broken peer; the page never sends data frames
                    }
                    if (masked && !await ReadExactAsync(mask, 0, 4).ConfigureAwait(false))
                    {
                        break;
                    }

                    bool isControl = (opcode & 0x8) != 0;
                    if (isControl)
                    {
                        int len = (int)length;
                        if (len > 125)
                        {
                            break; // RFC 6455 violation
                        }
                        if (len > 0 && !await ReadExactAsync(control, 0, len).ConfigureAwait(false))
                        {
                            break;
                        }
                        if (masked)
                        {
                            Unmask(control, len, mask);
                        }
                        if (opcode == 0x8)
                        {
                            // Close: echo the status code so the peer's close
                            // handshake completes cleanly, then tear down.
                            int echoLen = Math.Min(len, 2);
                            byte[] close = new byte[2 + echoLen];
                            close[0] = 0x88;
                            close[1] = (byte)echoLen;
                            Buffer.BlockCopy(control, 0, close, 2, echoLen);
                            await WriteBoundedAsync(close, 0, close.Length, _cts.Token).ConfigureAwait(false);
                            break;
                        }
                        if (opcode == 0x9)
                        {
                            // Ping: pong with the same payload.
                            byte[] pong = new byte[2 + len];
                            pong[0] = 0x8A;
                            pong[1] = (byte)len;
                            Buffer.BlockCopy(control, 0, pong, 2, len);
                            if (!await WriteBoundedAsync(pong, 0, pong.Length, _cts.Token).ConfigureAwait(false))
                            {
                                break;
                            }
                        }
                        // 0xA (pong, e.g. ClientWebSocket keep-alives): ignore.
                        continue;
                    }

                    long remaining = length;
                    if (scratch == null && remaining > 0)
                    {
                        scratch = new byte[4096];
                    }
                    while (remaining > 0)
                    {
                        int n = await _stream
                            .ReadAsync(scratch, 0, (int)Math.Min(remaining, scratch.Length), CancellationToken.None)
                            .ConfigureAwait(false);
                        if (n <= 0)
                        {
                            remaining = -1;
                            break;
                        }
                        remaining -= n;
                    }
                    if (remaining < 0)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Socket closed under us (Drop or peer reset).
            }
            finally
            {
                Drop();
            }
        }

        private async Task<bool> ReadExactAsync(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = await _stream.ReadAsync(buffer, offset, count, CancellationToken.None).ConfigureAwait(false);
                if (n <= 0)
                {
                    return false;
                }
                offset += n;
                count -= n;
            }
            return true;
        }

        private static void Unmask(byte[] buffer, int length, byte[] mask)
        {
            for (int i = 0; i < length; i++)
            {
                buffer[i] ^= mask[i & 3];
            }
        }
    }

    /// <summary>
    /// Keeps deliberately abandoned tasks (e.g. a write outlived by its
    /// timeout) from surfacing as unobserved task exceptions.
    /// </summary>
    internal static class DetachedTask
    {
        internal static void Observe(Task task)
        {
            task.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
