// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uniflag.Spike
{
    /// <summary>
    /// M2a throwaway spike: a localhost-only HttpListener that pushes synthetic
    /// 3072-byte RGB888 frames (32x32, row-major, top-left origin) at 30 fps over
    /// a raw binary WebSocket, so the overlay test page can measure real fps
    /// inside desktop Chrome/Edge and the DashStudio Web Page View.
    ///
    /// The synthetic pattern mirrors <c>paint_test_pattern</c> in
    /// <c>sim/src/main.rs</c> (slow-scrolling colour gradient plus a single white
    /// cursor pixel advancing one position per frame) so tearing, latency and
    /// stalls are visible at a glance and comparable with the M2b CDC spike.
    ///
    /// This whole file is measurement scaffolding: it dies after the M2a verdict
    /// lands in docs/web-overlay.md. The production page/WS design is M5.
    /// </summary>
    public sealed class OverlaySpikeServer
    {
        private const int PanelWidth = 32;
        private const int PanelHeight = 32;
        private const int FrameBytes = PanelWidth * PanelHeight * 3; // 3072
        private const int TargetFps = 30;

        private readonly object _gate = new object();
        private readonly List<WebSocket> _clients = new List<WebSocket>();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private Task _frameTask;
        private volatile string _status = "Stopped";

        /// <summary>
        /// Optional directory containing the overlay test page; when set and
        /// <c>index.html</c> exists there, GET / serves it. Otherwise GET /
        /// returns a plain-text note (the page is self-contained and also works
        /// straight from file://). Real page hosting is designed in M5.
        /// </summary>
        public string PageDirectory { get; set; }

        /// <summary>
        /// Human-readable state for the settings tab (M5 will surface it).
        /// Never throws; bind failures land here instead of crashing SimHub.
        /// </summary>
        public string Status => _status;

        /// <summary>
        /// Idempotent, thread-safe. On bind failure (port in use, URL ACL, bad
        /// port) it sets <see cref="Status"/> and returns without throwing.
        /// </summary>
        public void Start(int port)
        {
            lock (_gate)
            {
                if (_listener != null)
                {
                    return; // already running
                }
                if (port < 1 || port > 65535)
                {
                    _status = "Invalid port: " + port;
                    return;
                }

                string prefix = "http://127.0.0.1:" + port + "/";
                var listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                }
                catch (Exception ex)
                {
                    // HttpListenerException (port in use / ACL), PlatformNotSupported, ...
                    _status = "Bind failed on " + prefix + ": " + ex.Message;
                    try { listener.Close(); } catch { /* best-effort teardown */ }
                    return;
                }

                _listener = listener;
                _cts = new CancellationTokenSource();
                CancellationToken ct = _cts.Token;
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, ct));
                _frameTask = Task.Run(() => FrameLoopAsync(ct));
                _status = "Listening on " + prefix + " (frames on /ws)";
            }
        }

        /// <summary>
        /// Idempotent, thread-safe. Blocks briefly (bounded) for the loops to
        /// wind down; every worker uses ConfigureAwait(false), so calling this
        /// from SimHub's UI thread cannot deadlock.
        /// </summary>
        public void Stop()
        {
            HttpListener listener;
            CancellationTokenSource cts;
            Task acceptTask;
            Task frameTask;
            lock (_gate)
            {
                listener = _listener;
                cts = _cts;
                acceptTask = _acceptTask;
                frameTask = _frameTask;
                _listener = null;
                _cts = null;
                _acceptTask = null;
                _frameTask = null;
            }
            if (listener == null)
            {
                _status = "Stopped";
                return;
            }

            try { cts.Cancel(); } catch { /* already disposed */ }
            try { listener.Stop(); } catch { /* aborts pending GetContextAsync */ }

            WebSocket[] clients;
            lock (_clients)
            {
                clients = _clients.ToArray();
                _clients.Clear();
            }
            foreach (WebSocket ws in clients)
            {
                try { ws.Abort(); } catch { /* spike: no graceful close on shutdown */ }
                try { ws.Dispose(); } catch { }
            }

            var pending = new List<Task>(2);
            if (acceptTask != null) pending.Add(acceptTask);
            if (frameTask != null) pending.Add(frameTask);
            try { Task.WaitAll(pending.ToArray(), 2000); } catch { /* loops swallow their own errors */ }

            try { listener.Close(); } catch { }
            try { cts.Dispose(); } catch { }
            _status = "Stopped";
        }

        // ---------------------------------------------------------------------
        // HTTP side
        // ---------------------------------------------------------------------

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    // On .NET Framework a single aborted/reset client
                    // connection can throw HttpListenerException while the
                    // listener stays healthy — only treat exceptions as
                    // shutdown when we actually are shutting down.
                    if (ct.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }
                    continue;
                }
                _ = Task.Run(() => HandleContextAsync(ctx, ct));
            }
            if (!ct.IsCancellationRequested)
            {
                _status = "Accept loop exited unexpectedly; Stop/Start the spike server.";
            }
        }

        private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;

                if (path == "/ws")
                {
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        await WriteTextAsync(ctx.Response, 400,
                            "Expected a WebSocket upgrade on /ws.\n").ConfigureAwait(false);
                        return;
                    }
                    // Requires Windows 8+ http.sys WebSocket support — fine, the
                    // whole project already targets Windows-only SimHub hosts.
                    HttpListenerWebSocketContext wsCtx =
                        await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                    WebSocket ws = wsCtx.WebSocket;
                    lock (_clients)
                    {
                        _clients.Add(ws);
                    }
                    await DrainClientAsync(ws, ct).ConfigureAwait(false);
                    return;
                }

                if (path == "/")
                {
                    string dir = PageDirectory;
                    string file = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "index.html");
                    if (file != null && File.Exists(file))
                    {
                        byte[] body = File.ReadAllBytes(file);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream
                            .WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
                        ctx.Response.Close();
                    }
                    else
                    {
                        await WriteTextAsync(ctx.Response, 200,
                            "uniflag M2a overlay spike server.\n" +
                            "No page configured on this endpoint (PageDirectory unset or index.html missing).\n" +
                            "The test page is self-contained: open overlay/index.html via file:// (or\n" +
                            "`just overlay-serve`) and point it at ws://127.0.0.1:<port>/ws for frames.\n").ConfigureAwait(false);
                    }
                    return;
                }

                await WriteTextAsync(ctx.Response, 404, "Not found.\n").ConfigureAwait(false);
            }
            catch
            {
                // Spike: a broken request must never take SimHub down.
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, int status, string body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                response.StatusCode = status;
                response.ContentType = "text/plain; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.Close();
            }
            catch
            {
                try { response.Abort(); } catch { }
            }
        }

        // ---------------------------------------------------------------------
        // WebSocket side
        // ---------------------------------------------------------------------

        /// <summary>
        /// Per-client receive loop: the page never sends data, but pumping
        /// ReceiveAsync is what processes the client's close handshake (and
        /// pings) so a closed tab is noticed promptly instead of on the next
        /// failed send. One concurrent send (frame loop) plus one concurrent
        /// receive (here) per socket is explicitly allowed by WebSocket.
        /// </summary>
        private async Task DrainClientAsync(WebSocket ws, CancellationToken ct)
        {
            var buf = new byte[512];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result =
                        await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }
                        break;
                    }
                    // Anything else the page sends is ignored — no protocol on
                    // the WS in M2a beyond raw frames server→client.
                }
            }
            catch
            {
                // Cancellation, abrupt disconnect, aborted socket — all mean "gone".
            }
            finally
            {
                RemoveClient(ws);
            }
        }

        private void RemoveClient(WebSocket ws)
        {
            bool removed;
            lock (_clients)
            {
                removed = _clients.Remove(ws);
            }
            if (removed)
            {
                try { ws.Dispose(); } catch { }
            }
        }

        // ---------------------------------------------------------------------
        // Frame loop
        // ---------------------------------------------------------------------

        private async Task FrameLoopAsync(CancellationToken ct)
        {
            var frame = new byte[FrameBytes];
            long frameIndex = 0;
            long periodTicks = Stopwatch.Frequency / TargetFps;
            Stopwatch clock = Stopwatch.StartNew();
            // Monotonic deadline: frame n is due at start + n*period. Sleeping
            // toward the absolute deadline (instead of Task.Delay(33) per frame)
            // means timer slop never accumulates into drift; Task.Delay's ~15 ms
            // granularity only adds per-frame jitter, and the honest measurement
            // happens page-side anyway.
            long nextDue = clock.ElapsedTicks;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    nextDue += periodTicks;

                    PaintTestPattern(frame, frameIndex);
                    await BroadcastAsync(frame, ct).ConfigureAwait(false);
                    frameIndex++;

                    long now = clock.ElapsedTicks;
                    if (now > nextDue + 4 * periodTicks)
                    {
                        // Fell far behind (GC pause, stalled client, debugger):
                        // resynchronise rather than bursting catch-up frames.
                        nextDue = now;
                        continue;
                    }
                    long remainingMs = (nextDue - now) * 1000 / Stopwatch.Frequency;
                    if (remainingMs > 0)
                    {
                        await Task.Delay((int)remainingMs, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop() requested.
            }
            catch
            {
                // Spike: never let the frame loop kill the host process.
            }
        }

        /// <summary>
        /// Sequential broadcast: each send is awaited, so a socket never sees
        /// overlapping SendAsync calls, and the shared frame buffer is not
        /// repainted mid-send. A stalled client therefore delays the frame tick
        /// for everyone — acceptable for the spike, where the test matrix runs
        /// one client at a time; dead sockets are dropped on send failure.
        /// </summary>
        private async Task BroadcastAsync(byte[] frame, CancellationToken ct)
        {
            WebSocket[] clients;
            lock (_clients)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
                clients = _clients.ToArray();
            }

            var segment = new ArraySegment<byte>(frame);
            foreach (WebSocket ws in clients)
            {
                if (ws.State != WebSocketState.Open)
                {
                    RemoveClient(ws);
                    continue;
                }
                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Binary,
                        endOfMessage: true, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // Stop() — let the frame loop wind down.
                }
                catch
                {
                    RemoveClient(ws);
                }
            }
        }

        /// <summary>
        /// Byte-for-byte port of <c>paint_test_pattern</c> in sim/src/main.rs:
        /// slow-scrolling gradient plus a white cursor pixel advancing one
        /// position per frame. RGB888, row-major, top-left origin.
        /// </summary>
        private static void PaintTestPattern(byte[] payload, long frame)
        {
            long f = frame;
            for (int y = 0; y < PanelHeight; y++)
            {
                for (int x = 0; x < PanelWidth; x++)
                {
                    int i = (y * PanelWidth + x) * 3;
                    payload[i] = (byte)((x * 8 + f * 2) & 0xFF);
                    payload[i + 1] = (byte)((y * 8 + 256 - (f & 0xFF)) & 0xFF);
                    payload[i + 2] = (byte)(((x + y) * 4) & 0xFF);
                }
            }
            int cursor = (int)(f % (PanelWidth * PanelHeight));
            int ci = cursor * 3;
            payload[ci] = 0xFF;
            payload[ci + 1] = 0xFF;
            payload[ci + 2] = 0xFF;
        }
    }
}
