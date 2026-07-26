// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Renderer-loop unit tests: frame-index monotonicity, sink isolation (one
// broken sink must not kill the loop), the sink-refcounted lifecycle, and
// the input modes (blank, connected-idle). Pure Uniflag.Rendering — no WPF,
// no SimHub assemblies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;
using Xunit;
using Session = Uniflag.Rendering.Session;

namespace Uniflag.Tests
{
    public class RendererLoopTests
    {
        /// <summary>
        /// Records the indices it is handed and keeps a copy of the most
        /// recent pixels. Copying inside <see cref="OnFrame"/> is what every
        /// production sink does — the loop hands out its live paint buffer
        /// and reuses it on the next tick.
        /// </summary>
        private sealed class RecordingSink : IFrameSink
        {
            private readonly object _gate = new object();
            private readonly List<long> _indices = new List<long>();
            private readonly byte[] _latest = new byte[FrameBuffer.ByteLength];
            private long _latestIndex = -1;
            private int _badBuffers;

            public void OnFrame(byte[] rgb888, long frameIndex)
            {
                lock (_gate)
                {
                    if (rgb888 == null || rgb888.Length != FrameBuffer.ByteLength)
                    {
                        _badBuffers++;
                    }
                    else
                    {
                        Buffer.BlockCopy(rgb888, 0, _latest, 0, FrameBuffer.ByteLength);
                        _latestIndex = frameIndex;
                    }
                    _indices.Add(frameIndex);
                }
            }

            /// <summary>
            /// Copy the most recent frame out; returns its index, or -1 if
            /// none has arrived yet.
            /// </summary>
            public long Latest(byte[] destination)
            {
                lock (_gate)
                {
                    if (_latestIndex >= 0)
                    {
                        Buffer.BlockCopy(_latest, 0, destination, 0, FrameBuffer.ByteLength);
                    }
                    return _latestIndex;
                }
            }

            public int Count
            {
                get
                {
                    lock (_gate)
                    {
                        return _indices.Count;
                    }
                }
            }

            public int BadBuffers
            {
                get
                {
                    lock (_gate)
                    {
                        return _badBuffers;
                    }
                }
            }

            public long[] Indices()
            {
                lock (_gate)
                {
                    return _indices.ToArray();
                }
            }
        }

        private sealed class ThrowingSink : IFrameSink
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public void OnFrame(byte[] rgb888, long frameIndex)
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("deliberately broken sink");
            }
        }

        private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"timed out waiting for {what}");
                Thread.Sleep(10);
            }
        }

        [Fact]
        public void FrameIndicesAreStrictlyIncreasing()
        {
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            loop.AddSink(sink);
            WaitUntil(() => sink.Count >= 20, "20 frames");
            loop.RemoveSink(sink);

            long[] indices = sink.Indices();
            Assert.True(indices.Length >= 20, "expected at least 20 recorded frames");
            for (int i = 1; i < indices.Length; i++)
            {
                // Strictly increasing; gaps are legal (skipped ticks under
                // load), going backwards or repeating never is.
                Assert.True(
                    indices[i] > indices[i - 1],
                    $"frame index {indices[i]} at position {i} not greater than predecessor {indices[i - 1]}");
            }
            Assert.Equal(0, sink.BadBuffers);
        }

        [Fact]
        public void ThrowingSinkDoesNotStopTheLoopOrOtherSinks()
        {
            using var loop = new RendererLoop();
            var broken = new ThrowingSink();
            var healthy = new RecordingSink();
            IFrameSink faulted = null;
            loop.SinkFaulted += (sink, ex) => Volatile.Write(ref faulted, sink);

            loop.AddSink(broken);
            loop.AddSink(healthy);
            WaitUntil(
                () => healthy.Count >= 10 && broken.Calls >= 10,
                "10 frames through both the healthy and the throwing sink");

            Assert.True(loop.IsRunning);
            Assert.Same(broken, Volatile.Read(ref faulted));
            Assert.Equal(0, healthy.BadBuffers);
        }

        [Fact]
        public void LoopRunsOnlyWhileASinkIsRegistered()
        {
            using var loop = new RendererLoop();
            Assert.False(loop.IsRunning);

            var sink = new RecordingSink();
            loop.AddSink(sink);
            Assert.True(loop.IsRunning);
            WaitUntil(() => sink.Count >= 1, "the first frame");

            // RemoveSink joins the render thread, so no OnFrame call can
            // arrive after it returns.
            loop.RemoveSink(sink);
            Assert.False(loop.IsRunning);
            int settled = sink.Count;
            Thread.Sleep(100);
            Assert.Equal(settled, sink.Count);
        }

        [Fact]
        public void TheDefaultInputPaintsTheBootDarkPanel()
        {
            // The default input is the blank mode, which paints every pixel
            // black (the firmware boot-dark posture) — deterministic bytes
            // without pinning any animation timing.
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            var dest = new byte[FrameBuffer.ByteLength];
            Assert.Equal(-1, sink.Latest(dest));

            loop.AddSink(sink);
            WaitUntil(() => sink.Count >= 1, "the first frame");
            long index = sink.Latest(dest);
            loop.RemoveSink(sink);

            Assert.True(index >= 0, "expected a delivered frame index");
            Assert.All(dest, b => Assert.Equal((byte)0, b));
        }

        // The frame predicate below holds at every SETTLED frame (the idle
        // beacon's breathe never leaves its range); the onset flash makes the
        // first ~8 frames white, which the polling WaitForFrame simply skips.

        // Connected-idle (docs/flag-grammar.md §7b): the teal docked beacon —
        // cores (15,30)/(16,30), shoulders (14,30)/(17,30), halos
        // (15,29)/(16,29), all dim teal (R == 0, faint G and B), everything
        // else black — true at any frame.
        private static bool IsConnectedIdle(byte[] frame)
        {
            for (int p = 0; p < FrameBuffer.Width * FrameBuffer.Height; p++)
            {
                int x = p % FrameBuffer.Width;
                int y = p / FrameBuffer.Width;
                int i = p * 3;
                bool beacon = (y == 30 && x >= 14 && x <= 17) || (y == 29 && (x == 15 || x == 16));
                if (beacon)
                {
                    if (frame[i] != 0
                        || frame[i + 1] < 1 || frame[i + 1] > 24
                        || frame[i + 2] < 1 || frame[i + 2] > 18)
                    {
                        return false;
                    }
                }
                else if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Wait until the frame the sink last received satisfies
        /// <paramref name="predicate"/> (input changes latch at the next
        /// tick, so a matching frame appears within a tick or two).
        /// </summary>
        private static void WaitForFrame(RecordingSink sink, Func<byte[], bool> predicate, string what)
        {
            var dest = new byte[FrameBuffer.ByteLength];
            WaitUntil(() => sink.Latest(dest) >= 0 && predicate(dest), what);
        }

        [Fact]
        public void ConnectedIdleModePaintsTheIdleMarker()
        {
            using var loop = new RendererLoop();
            loop.SetConnectedIdle();
            var sink = new RecordingSink();
            loop.AddSink(sink);
            WaitForFrame(sink, IsConnectedIdle, "the §7b connected-idle frame");
            loop.RemoveSink(sink);
        }

        [Fact]
        public void SetStatePaintsTheLiveFlag()
        {
            // Blue static base: scale_rgb((0,64,255), 150..255) — every
            // pixel has R == 0 and B > 0 at any settled frame.
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            loop.AddSink(sink);

            SignalState state = SignalState.Default;
            state.Flag = TrackFlag.Blue;
            state.Session = Session.Racing;
            loop.SetState(state);

            WaitForFrame(
                sink,
                frame =>
                {
                    for (int i = 0; i < frame.Length; i += 3)
                    {
                        if (frame[i] != 0 || frame[i + 2] == 0)
                        {
                            return false;
                        }
                    }
                    return true;
                },
                "the blue field frame");
            loop.RemoveSink(sink);
        }

        [Fact]
        public void ClockResumesAcrossStopStartCycles()
        {
            using var loop = new RendererLoop();
            var first = new RecordingSink();
            loop.AddSink(first);
            WaitUntil(() => first.Count >= 2, "two frames in the first run");
            loop.RemoveSink(first);

            var second = new RecordingSink();
            loop.AddSink(second);
            WaitUntil(() => second.Count >= 2, "two frames in the second run");
            loop.RemoveSink(second);

            long[] firstRun = first.Indices();
            long[] secondRun = second.Indices();
            // The frame counter persists across sink-refcount restarts —
            // the second run continues after the first, never rewinds.
            Assert.True(
                secondRun[0] > firstRun[firstRun.Length - 1],
                $"second run started at {secondRun[0]}, not after {firstRun[firstRun.Length - 1]}");
        }
    }
}
