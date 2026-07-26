// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Web overlay server tests: frame delivery to a real stream client,
// graceful bind failure, the loopback-only binding, the sink registration
// refcount (first client registers, last disconnect unregisters), the
// embedded page's single-source contract, and port rebindability after
// Stop (SimHub cycles End/Init at every game change).
//
// CI-safe by construction: every test binds an ephemeral port (Start(0)),
// and the server's raw TcpListener needs no http.sys URL ACL, so nothing
// here requires elevation. The production port constant stays 8972.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uniflag.Rendering;
using Uniflag.Web;
using Xunit;

namespace Uniflag.Tests
{
    public class OverlayWebServerTests
    {
        private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"timed out waiting for {what}");
                Thread.Sleep(10);
            }
        }

        private static byte[] PatternFrame(int seed)
        {
            var frame = new byte[FrameBuffer.ByteLength];
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)((i + seed) % 251);
            }
            return frame;
        }

        [Fact]
        public async Task DeliversFramesToAStreamClientWithEvenTickDecimation()
        {
            var host = new CountingSinkHost();
            var server = new OverlayWebServer(host);
            try
            {
                server.Start(0);
                Assert.True(server.IsListening, server.StatusText);

                using (OverlayStreamClient client = await OverlayStreamClient.ConnectAsync(server))
                {
                    WaitUntil(() => server.ClientCount == 1, "the client to be admitted");

                    // Odd tick index alone first: decimated to 30 fps, it
                    // must never reach the wire. Nothing else is in flight,
                    // so any message inside the grace window means the
                    // parity filter regressed. (Posting the even frame
                    // back-to-back instead would let the depth-one
                    // newest-frame-wins slot coalesce the odd frame away
                    // before the send loop reads it, masking a broken
                    // filter.)
                    byte[] dropped = PatternFrame(7);
                    byte[] expected = PatternFrame(0);
                    server.OnFrame(dropped, 1);
                    Task<byte[]> receive = client.ReadFrameAsync();
                    Task winner = await Task.WhenAny(receive, Task.Delay(1000));
                    Assert.NotSame(receive, winner); // an odd tick reached the wire

                    // The even tick that follows must arrive byte-exact.
                    server.OnFrame(expected, 2);
                    byte[] received = await receive;
                    Assert.Equal(FrameBuffer.ByteLength, received.Length);
                    Assert.Equal(expected, received);
                }
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public async Task DripFedRequestHeadersAreCutByATotalDeadline()
        {
            // Slowloris guard: a client that trickles one header byte every
            // few hundred ms never lets an individual read time out, so the
            // header read must enforce one deadline across the whole request
            // (~5 s). Before that deadline existed, this connection stayed
            // open indefinitely.
            var server = new OverlayWebServer(new CountingSinkHost());
            try
            {
                server.Start(0);
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port);
                    NetworkStream stream = tcp.GetStream();
                    byte[] prefix = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX-Drip: ");
                    stream.Write(prefix, 0, prefix.Length);

                    bool closed = false;
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 20000)
                    {
                        Socket socket = tcp.Client;
                        if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                        {
                            closed = true; // FIN: the server closed on us
                            break;
                        }
                        try
                        {
                            // One header byte per tick; never completes the
                            // request, always beats a per-read timeout.
                            stream.Write(prefix, 0, 1);
                        }
                        catch (IOException)
                        {
                            closed = true; // RST: the server closed on us
                            break;
                        }
                        await Task.Delay(200);
                    }
                    Assert.True(closed, "the server never closed a drip-fed connection");
                }
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public void BindFailureIsAStatusNeverAThrow()
        {
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            try
            {
                int takenPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
                var server = new OverlayWebServer(new CountingSinkHost());

                Exception ex = Record.Exception(() => server.Start(takenPort));

                Assert.Null(ex);
                Assert.False(server.IsListening);
                Assert.Contains("Bind failed", server.StatusText);
                Assert.Contains(takenPort.ToString(), server.StatusText);
                // Stop after a failed Start must also be a no-op, not a throw.
                Assert.Null(Record.Exception(() => server.Stop()));
            }
            finally
            {
                blocker.Stop();
            }
        }

        [Fact]
        public void BindsLoopbackOnly()
        {
            var server = new OverlayWebServer(new CountingSinkHost());
            try
            {
                server.Start(0);
                // The security boundary of the whole feature: the listener is
                // bound to 127.0.0.1, never 0.0.0.0/any.
                Assert.Equal(IPAddress.Loopback, server.LocalEndPoint.Address);
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public async Task SinkRegistrationFollowsFirstAndLastClient()
        {
            var host = new CountingSinkHost();
            var server = new OverlayWebServer(host);
            try
            {
                server.Start(0);
                Assert.Equal(0, host.Adds);

                OverlayStreamClient first = await OverlayStreamClient.ConnectAsync(server);
                OverlayStreamClient second = await OverlayStreamClient.ConnectAsync(server);
                try
                {
                    WaitUntil(() => server.ClientCount == 2, "both clients to be admitted");
                    Assert.Equal(1, host.Adds); // one sink for N clients
                    Assert.Equal(0, host.Removes);

                    // Nothing is ever posted in this test: disconnect
                    // detection must not depend on a frame write failing.
                    first.Dispose();
                    WaitUntil(() => server.ClientCount == 1, "the first client to be reaped");
                    Assert.Equal(0, host.Removes); // still one watcher

                    second.Dispose();
                    WaitUntil(
                        () => server.ClientCount == 0 && host.Removes == 1,
                        "the last disconnect to unregister the sink");
                    Assert.Equal(1, host.Adds);
                }
                finally
                {
                    first.Dispose();
                    second.Dispose();
                }
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public async Task SinkRegistrationDrivesTheRealRendererLoop()
        {
            // Same refcount contract against the production seam: the render
            // thread runs exactly while a web client is watching.
            using (var renderer = new RendererLoop())
            {
                var server = new OverlayWebServer(renderer);
                try
                {
                    server.Start(0);
                    Assert.False(renderer.IsRunning);

                    using (OverlayStreamClient client = await OverlayStreamClient.ConnectAsync(server))
                    {
                        WaitUntil(() => renderer.IsRunning, "the renderer to start with the first client");
                        // Live frames flow end-to-end: renderer -> sink ->
                        // decimation -> stream. Content is whatever the blank
                        // mode paints; the size contract is what matters.
                        byte[] frame = await client.ReadFrameAsync();
                        Assert.Equal(FrameBuffer.ByteLength, frame.Length);

                        client.Dispose();
                        WaitUntil(() => !renderer.IsRunning, "the renderer to stop with the last client");
                    }
                }
                finally
                {
                    server.Stop();
                }
            }
        }

        [Fact]
        public void ServesTheEmbeddedPageWithNoCacheHeaders()
        {
            var server = new OverlayWebServer(new CountingSinkHost());
            try
            {
                server.Start(0);
                var request = (HttpWebRequest)WebRequest.Create(
                    $"http://127.0.0.1:{server.LocalEndPoint.Port}/?v=1");
                request.Timeout = 10000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var memory = new MemoryStream())
                {
                    response.GetResponseStream().CopyTo(memory);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.StartsWith("text/html", response.ContentType);
                    // The Web Page View caches hard across plugin updates;
                    // the page must always come with no-cache headers.
                    Assert.Contains("no-store", response.Headers["Cache-Control"]);

                    // Single-source contract: the served bytes ARE the
                    // repo's overlay/index.html (embedded at build time).
                    byte[] repoPage = File.ReadAllBytes(
                        Path.Combine(RepoPaths.RepoRoot, "overlay", "index.html"));
                    Assert.Equal(repoPage, memory.ToArray());
                }
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public async Task StopLeavesThePortImmediatelyRebindable()
        {
            // SimHub rebuilds plugins (End then Init) at every game change: a
            // leaked socket would break the very next game switch. A live
            // client at Stop time exercises the forced-teardown path.
            var server = new OverlayWebServer(new CountingSinkHost());
            server.Start(0);
            int port = server.LocalEndPoint.Port;
            OverlayStreamClient client = await OverlayStreamClient.ConnectAsync(server);
            try
            {
                WaitUntil(() => server.ClientCount == 1, "the client to be admitted");
            }
            finally
            {
                server.Stop();
            }
            client.Dispose();
            Assert.Equal("Stopped", server.StatusText);

            var reborn = new OverlayWebServer(new CountingSinkHost());
            try
            {
                reborn.Start(port);
                Assert.True(reborn.IsListening, reborn.StatusText);
                Assert.Equal(port, reborn.LocalEndPoint.Port);
            }
            finally
            {
                reborn.Stop();
            }
        }
    }
}
