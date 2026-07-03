// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Renderer-loop unit tests (docs/v2-plan.md M3 step 6): frame-index
// monotonicity, sink isolation (one broken sink must not kill the loop),
// the sink-refcounted lifecycle, and the published-frame pull contract.
// Pure Uniflag.Rendering — no WPF, no SimHub assemblies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Rendering;
using Xunit;

namespace Uniflag.Tests
{
    public class RendererLoopTests
    {
        private sealed class RecordingSink : IFrameSink
        {
            private readonly object _gate = new object();
            private readonly List<long> _indices = new List<long>();
            private int _badBuffers;

            public void OnFrame(byte[] rgb888, long frameIndex)
            {
                lock (_gate)
                {
                    if (rgb888 == null || rgb888.Length != FrameBuffer.ByteLength)
                    {
                        _badBuffers++;
                    }
                    _indices.Add(frameIndex);
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
        public void PublishesTheLatestCompletedFrame()
        {
            using var loop = new RendererLoop();
            var dest = new byte[FrameBuffer.ByteLength];
            Assert.Equal(-1, loop.CopyLatestFrame(dest));

            // The default input is disconnected, which paints every pixel
            // black (the firmware boot-dark posture) — deterministic bytes
            // without pinning any animation timing.
            var sink = new RecordingSink();
            loop.AddSink(sink);
            WaitUntil(() => sink.Count >= 1, "the first frame");
            long index = loop.CopyLatestFrame(dest);
            loop.RemoveSink(sink);

            Assert.True(index >= 0, "expected a published frame index");
            Assert.All(dest, b => Assert.Equal((byte)0, b));
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
