// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Test client for /stream. A raw socket rather than HttpClient because these
// tests care about the exact moment the connection dies: Dispose here is a
// real close, which is what a shut tab looks like to the server.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uniflag.Rendering;
using Uniflag.Web;

namespace Uniflag.Tests
{
    internal sealed class OverlayStreamClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private OverlayStreamClient(TcpClient tcp, NetworkStream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        internal static async Task<OverlayStreamClient> ConnectAsync(OverlayWebServer server)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port);
            NetworkStream stream = tcp.GetStream();
            byte[] request = Encoding.ASCII.GetBytes(
                "GET /stream HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, 0, request.Length);
            await SkipResponseHeadAsync(stream);
            return new OverlayStreamClient(tcp, stream);
        }

        /// <summary>Read the next whole 3072-byte frame off the stream body.</summary>
        internal async Task<byte[]> ReadFrameAsync()
        {
            var frame = new byte[FrameBuffer.ByteLength];
            int filled = 0;
            using (var cts = new CancellationTokenSource(10000))
            {
                while (filled < frame.Length)
                {
                    int n = await _stream.ReadAsync(frame, filled, frame.Length - filled, cts.Token);
                    if (n <= 0)
                    {
                        throw new IOException("stream closed mid-frame");
                    }
                    filled += n;
                }
            }
            return frame;
        }

        public void Dispose()
        {
            try { _tcp.Close(); } catch { }
        }

        /// <summary>One byte at a time, so no body bytes are swallowed into
        /// a read buffer.</summary>
        private static async Task SkipResponseHeadAsync(NetworkStream stream)
        {
            var one = new byte[1];
            int matched = 0;
            using (var cts = new CancellationTokenSource(10000))
            {
                while (matched < 4)
                {
                    int n = await stream.ReadAsync(one, 0, 1, cts.Token);
                    if (n <= 0)
                    {
                        throw new IOException("connection closed before the response head ended");
                    }
                    char c = (char)one[0];
                    bool expectCr = matched == 0 || matched == 2;
                    if (c == (expectCr ? '\r' : '\n'))
                    {
                        matched++;
                    }
                    else
                    {
                        matched = c == '\r' ? 1 : 0;
                    }
                }
            }
        }
    }
}
