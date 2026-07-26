// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// One overlay stream connection: a long-lived HTTP response whose body is
// raw 3072-byte RGB888 frames back to back. No framing layer — the record
// size is fixed and a frame is either written whole or the connection dies
// (see WriteBoundedAsync), so a reader cannot desync on a partial record.
//
// The send side is a depth-one newest-frame-wins queue — a flag display only
// cares about the latest frame — and a socket that refuses progress beyond
// SendTimeoutMs is disconnected so it can never stall the render thread or
// its siblings.

using System;
using System.IO;
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

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Action<OverlayClient> _onGone;
        private readonly CancellationTokenSource _cts;

        // Depth-one outbound queue (see LatestFrameSlot) plus its wake-up.
        private readonly LatestFrameSlot _slot = new LatestFrameSlot(FrameBuffer.ByteLength);
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);

        // Send snapshot: the slot's newest frame is taken into here so the
        // write is not holding its lock. The send loop is the only writer.
        private readonly byte[] _wire = new byte[FrameBuffer.ByteLength];

        private int _gone;

        internal OverlayClient(
            TcpClient tcp, NetworkStream stream, Action<OverlayClient> onGone, CancellationToken serverCt)
        {
            _tcp = tcp;
            _stream = stream;
            _onGone = onGone;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        }

        /// <summary>
        /// Queue the newest frame. Called on the render thread: never blocks
        /// beyond one bounded buffer copy behind a short lock, regardless of
        /// what the socket is doing.
        /// </summary>
        internal void Post(byte[] rgb888)
        {
            if (!_slot.Post(rgb888))
            {
                return; // coalesce: the queued wake-up sends this frame instead
            }
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // The slot's pending flag makes this unreachable; guarded so
                // the render thread can never be taken down from here.
            }
        }

        /// <summary>
        /// Run both pumps until the connection dies. Never throws; the
        /// server awaits this to know when the connection is fully drained.
        /// </summary>
        internal async Task RunAsync()
        {
            Task receive = DrainUntilPeerClosesAsync();
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

        // Send side

        private async Task SendLoopAsync()
        {
            CancellationToken ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                    if (!_slot.TryTake(_wire))
                    {
                        continue;
                    }
                    if (!await BoundedIo.WriteAsync(_stream, _wire, SendTimeoutMs, ct).ConfigureAwait(false))
                    {
                        Drop(); // closing the socket faults the abandoned write
                        break;
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

        // Receive side

        /// <summary>
        /// The page sends nothing, so this reads only to notice it leaving:
        /// a closed tab is a 0-byte read immediately, rather than whenever the
        /// next write fails. The sink refcount rides on that promptness — the
        /// render thread must stop even when nothing is being painted.
        /// </summary>
        private async Task DrainUntilPeerClosesAsync()
        {
            var scratch = new byte[256];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await _stream
                        .ReadAsync(scratch, 0, scratch.Length, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break; // FIN: the tab closed or navigated away
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

    /// <summary>
    /// The one bounded-write helper both overlay layers use: write the whole
    /// buffer or give up. False when the stream refuses progress for
    /// <paramref name="timeoutMs"/> — the abandoned write is observed, and
    /// the caller must close the socket to fault it, because net48 writes
    /// ignore cancellation once started.
    /// </summary>
    internal static class BoundedIo
    {
        internal static async Task<bool> WriteAsync(Stream stream, byte[] data, int timeoutMs, CancellationToken ct)
        {
            Task write = stream.WriteAsync(data, 0, data.Length, CancellationToken.None);
            if (!write.IsCompleted)
            {
                Task first = await Task.WhenAny(write, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                if (first != write)
                {
                    DetachedTask.Observe(write);
                    return false;
                }
            }
            await write.ConfigureAwait(false); // propagate socket faults
            return true;
        }
    }
}
