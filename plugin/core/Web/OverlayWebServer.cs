// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The production web overlay host: a localhost-only TCP
// listener that serves the LED-dot overlay page (embedded assembly resource,
// single-sourced from overlay/index.html) on GET / and streams the
// renderer's raw 3072-byte RGB888 frames from GET /stream.
//
// Raw TcpListener + hand-rolled HTTP on purpose: net48's HttpListener rides
// http.sys, whose URL-ACL rules can demand elevation to register a prefix,
// and the test suite must pass on an unprivileged CI runner. A plain socket
// has no such dependency, and binding IPAddress.Loopback makes the security
// boundary — localhost only, never reachable from the LAN — a single
// provable line.
//
// The frame transport is a long-lived HTTP response, not a WebSocket: the
// page only ever receives and the record size is fixed, so RFC 6455's
// framing, masking and control frames would buy nothing.
//
// Lifecycle: Start binds in plugin Init, Stop tears down in End. SimHub
// rebuilds plugins (End then Init) at every game change, so Stop must close
// every socket, wind down every worker, and leave the port immediately
// rebindable. Bind failure is a status string surfaced in the settings tab,
// never an exception into SimHub.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uniflag.Rendering;

namespace Uniflag.Web
{
    /// <summary>
    /// Localhost-only HTTP host for the browser/overlay virtual panel: it
    /// serves the page at <c>/</c> and streams raw frames from
    /// <c>/stream</c>. Implements <see cref="IFrameSink"/>; the server
    /// registers itself with the renderer (via <see cref="IFrameSinkHost"/>)
    /// while at least one stream client is connected — the render thread
    /// only runs when someone is watching.
    /// </summary>
    public sealed class OverlayWebServer : IFrameSink
    {
        /// <summary>
        /// The fixed production port (documented in docs/web-overlay.md and
        /// baked into the committed overlay dash). Tests pass 0 for an
        /// ephemeral port instead.
        /// </summary>
        public const int DefaultPort = 8972;

        /// <summary>
        /// Broadcast rate (docs/web-overlay.md). Sampled off the renderer's
        /// 60 fps clock by even-tick decimation in <see cref="OnFrame"/>.
        /// </summary>
        public const int BroadcastFps = RendererLoop.TargetFps / 2;

        /// <summary>
        /// Manifest resource name of the embedded overlay page; must match the
        /// LogicalName pinned in UniflagPlugin.csproj.
        /// </summary>
        public const string PageResourceName = "Uniflag.Web.index.html";

        private const int MaxRequestHeaderBytes = 16 * 1024;
        // One deadline across the whole request for header reads: a per-read
        // bound would reset on every byte, letting a drip-feed (slowloris)
        // client pin a socket and worker indefinitely. Writes bounded individually.
        private const int HttpIoTimeoutMs = 5000;

        private static readonly Lazy<byte[]> PageBytes =
            new Lazy<byte[]>(LoadPageResource, LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly IFrameSinkHost _host;

        // Listener lifecycle. _status is the bind-failure / stopped text;
        // null means "running" and StatusText composes URL + client count.
        private readonly object _gate = new object();
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private volatile string _status = "Stopped";

        // Connected (upgraded) clients. _clientSnapshot is copy-on-write so
        // the render thread broadcasts without taking a lock.
        private readonly object _clientsGate = new object();
        private readonly List<OverlayClient> _clients = new List<OverlayClient>();
        private volatile OverlayClient[] _clientSnapshot = Array.Empty<OverlayClient>();
        private bool _stopping;

        // Pre-upgrade connections (still in the HTTP phase), tracked so Stop
        // can abort them deterministically instead of waiting out their I/O
        // timeouts. Upgraded sockets move to _clients.
        private readonly ConcurrentDictionary<TcpClient, byte> _pendingConnections =
            new ConcurrentDictionary<TcpClient, byte>();

        // Serializes sink registration transitions so a first-connect racing
        // a last-disconnect converges on the registration that matches the
        // live client count. Never taken by OnFrame, so holding it across
        // RemoveSink (which joins the render thread) cannot deadlock.
        private readonly object _sinkGate = new object();
        private bool _sinkRegistered;

        public OverlayWebServer(IFrameSinkHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>Whether the listener is currently bound.</summary>
        public bool IsListening
        {
            get
            {
                lock (_gate)
                {
                    return _listener != null;
                }
            }
        }

        public int ClientCount => _clientSnapshot.Length;

        /// <summary>
        /// The bound endpoint — always a loopback address, with the actual
        /// port (which differs from the requested one only for port 0).
        /// Null when not listening.
        /// </summary>
        public IPEndPoint LocalEndPoint
        {
            get
            {
                lock (_gate)
                {
                    return _listener == null ? null : (IPEndPoint)_listener.LocalEndpoint;
                }
            }
        }

        /// <summary>
        /// Human-readable state for the settings tab: the URL and client
        /// count while running, otherwise the bind-failure or stopped text.
        /// Never throws.
        /// </summary>
        public string StatusText
        {
            get
            {
                string status = _status;
                if (status != null)
                {
                    return status;
                }
                IPEndPoint endpoint = LocalEndPoint;
                if (endpoint == null)
                {
                    return "Stopped";
                }
                int clients = ClientCount;
                return "Running on http://127.0.0.1:" + endpoint.Port + "/ - "
                    + (clients == 1 ? "1 client" : clients + " clients");
            }
        }

        /// <summary>
        /// Bind and start serving. Idempotent and thread-safe. On bind
        /// failure (port in use, sockets exhausted) the failure lands in
        /// <see cref="StatusText"/> and the method returns without throwing
        /// — the rest of the plugin must keep working.
        /// </summary>
        /// <param name="port">TCP port; 0 requests an ephemeral port (tests).</param>
        public void Start(int port = DefaultPort)
        {
            lock (_gate)
            {
                if (_listener != null)
                {
                    return;
                }
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start(16);
                }
                catch (Exception ex)
                {
                    // SocketException (port taken) or SecurityException.
                    _status = "Bind failed on 127.0.0.1:" + port + ": " + ex.Message;
                    try { listener.Stop(); } catch { }
                    return;
                }
                lock (_clientsGate)
                {
                    _stopping = false;
                }
                _listener = listener;
                _cts = new CancellationTokenSource();
                CancellationToken ct = _cts.Token;
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, ct));
                _status = null;
            }
        }

        /// <summary>
        /// Stop the listener, drop every connection, unregister the sink and
        /// wind down the workers (bounded wait). Idempotent and thread-safe;
        /// the port is rebindable as soon as this returns — SimHub calls
        /// End/Init back-to-back on every game change.
        /// </summary>
        public void Stop()
        {
            TcpListener listener;
            CancellationTokenSource cts;
            Task acceptTask;
            lock (_gate)
            {
                listener = _listener;
                cts = _cts;
                acceptTask = _acceptTask;
                _listener = null;
                _cts = null;
                _acceptTask = null;
            }
            if (listener == null)
            {
                return; // never started, or bind failed: keep that status text
            }

            OverlayClient[] clients;
            lock (_clientsGate)
            {
                // Connections that finish their upgrade after this point see
                // _stopping and drop themselves instead of being admitted.
                _stopping = true;
                clients = _clients.ToArray();
                _clients.Clear();
                _clientSnapshot = Array.Empty<OverlayClient>();
            }

            try { cts.Cancel(); } catch { }
            try { listener.Stop(); } catch { /* aborts the pending accept */ }

            foreach (KeyValuePair<TcpClient, byte> pending in _pendingConnections)
            {
                try { pending.Key.Close(); } catch { }
            }
            foreach (OverlayClient client in clients)
            {
                client.Drop();
            }

            lock (_sinkGate)
            {
                if (_sinkRegistered)
                {
                    _host.RemoveSink(this);
                    _sinkRegistered = false;
                }
            }

            if (acceptTask != null)
            {
                try { acceptTask.Wait(3000); } catch { }
            }
            try { cts.Dispose(); } catch { }
            _status = "Stopped";
        }

        /// <summary>
        /// <see cref="IFrameSink"/> entry point, called on the render thread
        /// at 60 fps and decimated to <see cref="BroadcastFps"/> via
        /// <see cref="HalfRate"/>. Never blocks: per-client work is one
        /// bounded buffer copy (see <see cref="OverlayClient.Post"/>).
        /// </summary>
        public void OnFrame(byte[] rgb888, long frameIndex)
        {
            if (HalfRate.Skip(frameIndex))
            {
                return;
            }
            OverlayClient[] clients = _clientSnapshot;
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i].Post(rgb888);
            }
        }

        // Accept / HTTP phase

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch
                {
                    // listener.Stop() aborts the pending accept; anything else
                    // (a client aborting mid-handshake) is transient unless
                    // the listener itself is no longer bound.
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    try
                    {
                        if (!listener.Server.IsBound)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                    continue;
                }
                TcpClient accepted = tcp;
                _ = Task.Run(() => HandleConnectionAsync(accepted, ct));
            }
        }

        private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
        {
            bool upgraded = false;
            _pendingConnections.TryAdd(tcp, 0);
            try
            {
                if (ct.IsCancellationRequested)
                {
                    return; // raced Stop's sweep of _pendingConnections
                }
                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();

                ParsedRequest request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }
                if (request.Method != "GET")
                {
                    await WriteTextResponseAsync(stream, "405 Method Not Allowed", "GET only.\n", ct)
                        .ConfigureAwait(false);
                    return;
                }
                if (request.Path == "/stream")
                {
                    if (!await WriteBoundedAsync(stream, StreamResponseHeader, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                    upgraded = true;
                    _pendingConnections.TryRemove(tcp, out _);
                    await ServeFrameStreamAsync(tcp, stream, ct).ConfigureAwait(false);
                    return;
                }
                if (request.Path == "/" || request.Path == "/index.html")
                {
                    await WritePageResponseAsync(stream, ct).ConfigureAwait(false);
                    return;
                }
                await WriteTextResponseAsync(stream, "404 Not Found", "Not found.\n", ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // One broken connection must never take the server (or SimHub) down.
            }
            finally
            {
                _pendingConnections.TryRemove(tcp, out _);
                if (!upgraded)
                {
                    try { tcp.Close(); } catch { }
                }
            }
        }

        private async Task ServeFrameStreamAsync(TcpClient tcp, NetworkStream stream, CancellationToken ct)
        {
            var client = new OverlayClient(tcp, stream, OnClientGone, ct);
            bool admitted;
            lock (_clientsGate)
            {
                admitted = !_stopping;
                if (admitted)
                {
                    _clients.Add(client);
                    _clientSnapshot = _clients.ToArray();
                }
            }
            if (!admitted)
            {
                client.Drop();
                return;
            }
            ReconcileSinkRegistration();
            await client.RunAsync().ConfigureAwait(false);
        }

        private void OnClientGone(OverlayClient client)
        {
            bool removed;
            lock (_clientsGate)
            {
                removed = _clients.Remove(client);
                if (removed)
                {
                    _clientSnapshot = _clients.ToArray();
                }
            }
            if (removed)
            {
                ReconcileSinkRegistration();
            }
        }

        /// <summary>
        /// Converge sink registration on "registered iff at least one client
        /// and not stopping". Transitions are serialized under _sinkGate and
        /// re-read the live count, so first-connect/last-disconnect races
        /// (in either order) always settle on the correct final state.
        /// </summary>
        private void ReconcileSinkRegistration()
        {
            lock (_sinkGate)
            {
                bool want;
                lock (_clientsGate)
                {
                    want = !_stopping && _clients.Count > 0;
                }
                if (want == _sinkRegistered)
                {
                    return;
                }
                if (want)
                {
                    _host.AddSink(this);
                }
                else
                {
                    _host.RemoveSink(this);
                }
                _sinkRegistered = want;
            }
        }

        // HTTP plumbing

        private sealed class ParsedRequest
        {
            internal string Method;
            internal string Path;
        }

        /// <summary>
        /// The /stream response head. The body that follows is raw 3072-byte
        /// frames forever, so there is no Content-Length and the connection
        /// ends only when one side closes.
        ///
        /// <para><c>Access-Control-Allow-Origin</c> is required by
        /// <c>just overlay-serve</c>, which serves a dev copy of the page
        /// from a different port: a WebSocket handshake was exempt from
        /// CORS, a fetch is not. It is a deliberate grant — any page the
        /// user visits can read this loopback frame stream. That was already
        /// true of the old WebSocket endpoint (the server never checked
        /// Origin), so the header makes an existing property explicit rather
        /// than adding one.</para>
        /// </summary>
        private static readonly byte[] StreamResponseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/octet-stream\r\n"
            + "Cache-Control: no-store\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Connection: close\r\n"
            + "\r\n");

        private static async Task<ParsedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] buffer = new byte[MaxRequestHeaderBytes];
            int used = 0;
            int headerEnd = -1;
            var deadline = Stopwatch.StartNew(); // total-request bound, never reset by progress
            while (headerEnd < 0)
            {
                if (used == buffer.Length)
                {
                    return null;
                }
                int remainingMs = HttpIoTimeoutMs - (int)deadline.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    return null; // drip-fed headers: deadline exhausted
                }
                Task<int> read = stream.ReadAsync(buffer, used, buffer.Length - used, CancellationToken.None);
                if (!read.IsCompleted)
                {
                    Task first = await Task.WhenAny(read, Task.Delay(remainingMs, ct)).ConfigureAwait(false);
                    if (first != read)
                    {
                        // Slow-header client (or Stop): caller closes the
                        // socket, which faults the abandoned read.
                        DetachedTask.Observe(read);
                        return null;
                    }
                }
                int n = await read.ConfigureAwait(false);
                if (n <= 0)
                {
                    return null;
                }
                int scanFrom = Math.Max(0, used - 3); // terminator may straddle reads
                used += n;
                headerEnd = FindHeaderEnd(buffer, scanFrom, used);
            }
            return ParseRequest(buffer, headerEnd);
        }

        private static int FindHeaderEnd(byte[] buffer, int from, int used)
        {
            for (int i = from; i + 3 < used; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                    && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static ParsedRequest ParseRequest(byte[] buffer, int headerEnd)
        {
            string text = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 3)
            {
                return null;
            }
            string target = parts[1];
            int query = target.IndexOf('?');
            return new ParsedRequest
            {
                Method = parts[0],
                Path = query < 0 ? target : target.Substring(0, query),
            };
        }

        private async Task WritePageResponseAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] page = PageBytes.Value;
            if (page == null)
            {
                await WriteTextResponseAsync(
                    stream, "500 Internal Server Error",
                    "Embedded overlay page resource missing from the plugin assembly.\n", ct)
                    .ConfigureAwait(false);
                return;
            }
            // no-cache on every page response: the DashStudio Web Page View
            // caches hard across plugin updates. Headers cover well-behaved
            // browsers; the dash URL's ?v= param covers the embedded view
            // (docs/web-overlay.md).
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + page.Length + "\r\n"
                + "Cache-Control: no-store, no-cache, must-revalidate\r\n"
                + "Pragma: no-cache\r\n"
                + "Expires: 0\r\n"
                + "Connection: close\r\n"
                + "\r\n");
            if (await WriteBoundedAsync(stream, header, ct).ConfigureAwait(false))
            {
                await WriteBoundedAsync(stream, page, ct).ConfigureAwait(false);
            }
        }

        private static async Task WriteTextResponseAsync(
            NetworkStream stream, string statusLine, string body, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + statusLine + "\r\n"
                + "Content-Type: text/plain; charset=utf-8\r\n"
                + "Content-Length: " + bytes.Length + "\r\n"
                + "Connection: close\r\n"
                + "\r\n");
            if (await WriteBoundedAsync(stream, header, ct).ConfigureAwait(false))
            {
                await WriteBoundedAsync(stream, bytes, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Bounded HTTP-phase write; returns false on timeout (caller's
        /// finally closes the socket, faulting the abandoned write).
        /// </summary>
        private static Task<bool> WriteBoundedAsync(NetworkStream stream, byte[] data, CancellationToken ct) =>
            BoundedIo.WriteAsync(stream, data, HttpIoTimeoutMs, ct);

        private static byte[] LoadPageResource()
        {
            using (Stream resource = typeof(OverlayWebServer).Assembly.GetManifestResourceStream(PageResourceName))
            {
                if (resource == null)
                {
                    return null; // served as a 500 — never a crash
                }
                using (var memory = new MemoryStream())
                {
                    resource.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }
    }
}
