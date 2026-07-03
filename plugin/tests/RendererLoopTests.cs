// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Renderer-loop unit tests (docs/v2-plan.md M3 step 6): frame-index
// monotonicity, sink isolation (one broken sink must not kill the loop),
// the sink-refcounted lifecycle, the published-frame pull contract, and the
// M4 input arbitration (normal vs override channel, connected-idle mode).
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

            // The default input is the blank mode, which paints every pixel
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

        // -------------------------------------------------------------------
        // M4 input arbitration. The frame predicates below hold at EVERY
        // frame index (static cloth-wave fills never strobe dark; the idle
        // marker's breathe never leaves its range), so no animation timing
        // is pinned.
        // -------------------------------------------------------------------

        private static RenderState LiveFlag(Flag flag)
        {
            RenderState state = RenderState.Default;
            state.Flag = flag;
            state.Session = Session.Racing;
            return state;
        }

        // Blue static base: scale_rgb((0,64,255), 150..255) — every pixel
        // has R == 0 and B > 0 at any frame.
        private static bool IsBlueFill(byte[] frame)
        {
            for (int i = 0; i < frame.Length; i += 3)
            {
                if (frame[i] != 0 || frame[i + 2] == 0)
                {
                    return false;
                }
            }
            return true;
        }

        // Yellow static base: scale_rgb((255,220,0), 150..255) — every pixel
        // has R > 0 and B == 0 at any frame.
        private static bool IsYellowFill(byte[] frame)
        {
            for (int i = 0; i < frame.Length; i += 3)
            {
                if (frame[i] == 0 || frame[i + 2] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        // Connected-idle (docs/effects-spec.md §7b): only (15,31) and
        // (16,31) lit, both dim blue within the breathe range (0, 2..6,
        // 8..24) — true at any frame.
        private static bool IsConnectedIdle(byte[] frame)
        {
            for (int p = 0; p < FrameBuffer.Width * FrameBuffer.Height; p++)
            {
                int x = p % FrameBuffer.Width;
                int y = p / FrameBuffer.Width;
                int i = p * 3;
                if (y == 31 && (x == 15 || x == 16))
                {
                    if (frame[i] != 0
                        || frame[i + 1] < 2 || frame[i + 1] > 6
                        || frame[i + 2] < 8 || frame[i + 2] > 24)
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

        private static bool IsAllBlack(byte[] frame)
        {
            foreach (byte b in frame)
            {
                if (b != 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Wait until the published frame satisfies <paramref name="predicate"/>
        /// (input changes latch at the next tick, so a matching frame
        /// appears within a tick or two).
        /// </summary>
        private static void WaitForFrame(RendererLoop loop, Func<byte[], bool> predicate, string what)
        {
            var dest = new byte[FrameBuffer.ByteLength];
            WaitUntil(() => loop.CopyLatestFrame(dest) >= 0 && predicate(dest), what);
        }

        [Fact]
        public void ConnectedIdleModePaintsTheIdleMarker()
        {
            using var loop = new RendererLoop();
            loop.SetConnectedIdle();
            var sink = new RecordingSink();
            loop.AddSink(sink);
            WaitForFrame(loop, IsConnectedIdle, "the §7b connected-idle frame");
            loop.RemoveSink(sink);
        }

        [Fact]
        public void OverrideWinsAndClearingFallsBackToTheNormalInput()
        {
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            loop.AddSink(sink);

            // Normal input: live blue.
            loop.SetState(LiveFlag(Flag.Blue), connected: true);
            WaitForFrame(loop, IsBlueFill, "the normal-input blue frame");

            // Override with live yellow — must clobber the normal view.
            loop.SetOverrideState(LiveFlag(Flag.Yellow), connected: true);
            WaitForFrame(loop, IsYellowFill, "the override yellow frame");

            // Normal input keeps updating underneath: it must NOT show.
            // Wait for at least two further ticks, then check the frame
            // painted after the normal-channel update is still the override.
            loop.SetState(LiveFlag(Flag.Blue), connected: true);
            var scratch = new byte[FrameBuffer.ByteLength];
            long seen = loop.CopyLatestFrame(scratch);
            WaitUntil(
                () => loop.CopyLatestFrame(scratch) >= seen + 2,
                "two ticks after the shadowed normal-channel update");
            Assert.True(IsYellowFill(scratch), "override must keep winning over normal-channel updates");

            // Clearing the override falls back to the last normal input.
            loop.ClearOverride();
            WaitForFrame(loop, IsBlueFill, "the blue frame after clearing the override");

            loop.RemoveSink(sink);
        }

        [Fact]
        public void ClearingTheOverrideWithoutNormalInputFallsBackToBlank()
        {
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            loop.AddSink(sink);

            loop.SetOverrideState(LiveFlag(Flag.Yellow), connected: true);
            WaitForFrame(loop, IsYellowFill, "the override yellow frame");

            // The normal channel was never fed: fall back to boot-dark.
            loop.ClearOverride();
            WaitForFrame(loop, IsAllBlack, "the blank frame after clearing the override");

            loop.RemoveSink(sink);
        }

        [Fact]
        public void OverrideWinsOverConnectedIdle()
        {
            using var loop = new RendererLoop();
            var sink = new RecordingSink();
            loop.AddSink(sink);

            // The DataUpdate arbitration case: cycler override active while
            // the telemetry path keeps reporting "no game".
            loop.SetOverrideState(LiveFlag(Flag.Yellow), connected: true);
            loop.SetConnectedIdle();
            WaitForFrame(loop, IsYellowFill, "the override frame despite connected-idle on the normal channel");

            loop.ClearOverride();
            WaitForFrame(loop, IsConnectedIdle, "the connected-idle frame after clearing the override");

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
