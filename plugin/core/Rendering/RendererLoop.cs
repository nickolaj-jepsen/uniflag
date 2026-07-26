// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The one-renderer→N-sinks core: a dedicated background thread ticks the
// 60 fps internal frame counter that all animation math assumes
// (docs/flag-grammar.md §4), paints the whole panel, and hands the finished
// 3072-byte RGB888 buffer to every registered IFrameSink (which must copy
// out before returning). Sinks sample this clock; the counter is never
// rebased to a sink's rate (30 fps USB, the overlay's measured fps, WPF's
// display cadence) — that would halve every strobe rate.
//
// WPF-free by contract: plugin/core/Rendering/ must stay loadable from plain
// xunit with no SimHub-assembly or System.Windows dependency.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Uniflag.Rendering.Grammar;

namespace Uniflag.Rendering
{
    /// <summary>
    /// What the renderer paints each tick — the idle model of
    /// docs/flag-grammar.md §7, from the plugin's point of view.
    /// </summary>
    public enum RenderInputMode
    {
        /// <summary>Boot-dark blank panel — the firmware's disconnected
        /// posture as seen host-side, and the initial mode.</summary>
        Blank,

        /// <summary>
        /// Plugin connected-idle (docs/flag-grammar.md §7b): renderer
        /// alive, no game session. Painted by
        /// <see cref="Idles.PaintConnectedIdle"/>.
        /// </summary>
        ConnectedIdle,

        /// <summary>Live game state through the Grammar compositor and <see cref="Painter"/>.</summary>
        Live,
    }

    /// <summary>
    /// The renderer loop. Lifecycle is ref-counted on sink registration: the
    /// render thread runs while at least one sink is registered and stops
    /// when the last one is removed (<see cref="Dispose"/> stops it
    /// unconditionally). The animation clock (frame index, flag age) is
    /// preserved across stop/start cycles.
    ///
    /// <para>Input is latched once at the top of every tick. Zero allocation
    /// per frame: the paint target and sink snapshot array are reused.</para>
    ///
    /// <para><b>Pacing:</b> monotonic <see cref="Stopwatch"/> deadlines, with
    /// missed ticks skipped rather than slewed, so animation rates stay
    /// wall-clock true when the thread falls behind.</para>
    /// </summary>
    public sealed class RendererLoop : IFrameSinkHost, IDisposable
    {
        /// <summary>
        /// The internal animation tick rate. Fixed by the Grammar animation
        /// math (docs/flag-grammar.md §4) — never derive it from a sink.
        /// </summary>
        public const int TargetFps = 60;

        // _sinkSnapshot is copy-on-write so the render thread reads it without locking.
        //
        // _gate also serializes thread lifecycle, and Stop joins before
        // releasing it — so two render threads can never share _back or
        // deliver duplicate indices. Joining under _gate is safe because the
        // render thread never takes it (it uses _publishGate / _inputGate).
        private readonly object _gate = new object();
        private readonly List<IFrameSink> _sinks = new List<IFrameSink>();
        private volatile IFrameSink[] _sinkSnapshot = Array.Empty<IFrameSink>();
        private Thread _thread;
        private ManualResetEventSlim _stop;
        private bool _disposed;

        // Latest input, latched once at the top of every tick. Boots blank
        // (firmware's boot-dark posture) until someone feeds it.
        private readonly object _inputGate = new object();
        private RenderInputMode _mode = RenderInputMode.Blank;
        private SignalState _state = SignalState.Default;

        // Handed to sinks directly: the IFrameSink borrow rule makes them
        // copy out before returning, and nothing repaints until the next tick.
        private readonly FrameBuffer _back = new FrameBuffer();

        // Next frame index to render. Interlocked so a stop/start cycle
        // resumes the clock instead of restarting it.
        private long _nextFrame;

        // Render-thread-only. Handed across stop/start cycles by the
        // Thread.Start/Join barriers.
        private readonly EnvelopeTracker _envelope = new EnvelopeTracker();

        /// <summary>
        /// Raised on the render thread when a sink's
        /// <see cref="IFrameSink.OnFrame"/> throws. The faulting sink stays
        /// registered and the loop keeps running; this exists so a host can
        /// log the failure. Handler exceptions are swallowed.
        /// </summary>
        public event Action<IFrameSink, Exception> SinkFaulted;

        /// <summary>Whether the render thread is currently running.</summary>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _thread != null;
                }
            }
        }

        /// <summary>
        /// Thread-safe. The envelope tracker diffs successive latched
        /// states, so docs/flag-grammar.md §4 transients follow automatically.
        /// </summary>
        public void SetState(SignalState state, bool connected)
        {
            lock (_inputGate)
            {
                _mode = connected ? RenderInputMode.Live : RenderInputMode.Blank;
                _state = state;
            }
        }

        /// <summary>
        /// Switch the input to the connected-idle marker
        /// (docs/flag-grammar.md §7b) — renderer alive, no game session.
        /// The latched state becomes <see cref="SignalState.Default"/> and
        /// the envelope resets, so a game session opening straight into a
        /// flag replays that flag's onset.
        /// </summary>
        public void SetConnectedIdle()
        {
            lock (_inputGate)
            {
                _mode = RenderInputMode.ConnectedIdle;
                _state = SignalState.Default;
            }
        }

        /// <summary>
        /// Register a sink and, if it is the first, start the render thread.
        /// Idempotent per sink instance. After <see cref="Dispose"/> this is
        /// a silent no-op (never throws into host teardown races).
        /// </summary>
        public void AddSink(IFrameSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }
            lock (_gate)
            {
                if (_disposed || _sinks.Contains(sink))
                {
                    return;
                }
                _sinks.Add(sink);
                _sinkSnapshot = _sinks.ToArray();
                if (_thread == null)
                {
                    StartLocked();
                }
            }
        }

        /// <summary>
        /// Unregister a sink; when the last one goes, the render thread is
        /// stopped and joined before this returns (so no
        /// <see cref="IFrameSink.OnFrame"/> call can arrive afterwards).
        /// Unknown sinks are ignored.
        /// </summary>
        public void RemoveSink(IFrameSink sink)
        {
            if (sink == null)
            {
                return;
            }
            lock (_gate)
            {
                if (!_sinks.Remove(sink))
                {
                    return;
                }
                _sinkSnapshot = _sinks.ToArray();
                if (_sinks.Count == 0)
                {
                    StopLocked();
                }
            }
        }

        /// <summary>
        /// Stop the render thread (regardless of registered sinks), drop all
        /// sinks, and make further <see cref="AddSink"/> calls no-ops.
        /// Idempotent.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _sinks.Clear();
                _sinkSnapshot = Array.Empty<IFrameSink>();
                StopLocked();
            }
        }

        // Thread lifecycle. Callers hold _gate; StopLocked leaves no thread
        // running, so StartLocked never sees a predecessor.

        private void StartLocked()
        {
            var stop = new ManualResetEventSlim(false);
            long startFrame = Interlocked.Read(ref _nextFrame);
            var thread = new Thread(() => Run(stop, startFrame))
            {
                IsBackground = true,
                Name = "uniflag-renderer",
            };
            _stop = stop;
            _thread = thread;
            thread.Start();
        }

        /// <summary>
        /// Signals and drains, so no <see cref="IFrameSink.OnFrame"/> can
        /// arrive after the caller releases <c>_gate</c>.
        /// </summary>
        private void StopLocked()
        {
            Thread thread = _thread;
            if (thread == null)
            {
                return;
            }
            _stop.Set();
            // Deliberately not disposed: the render thread may still be inside
            // Wait(), and a signalled-then-collected ManualResetEventSlim costs
            // nothing measurable.
            _stop = null;
            _thread = null;
            // Joining from the render thread itself would self-deadlock; that
            // only happens if a sink re-enters Add/RemoveSink, which the
            // IFrameSink contract forbids — degrade to a signal-only stop
            // rather than hanging.
            if (thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        private void Run(ManualResetEventSlim stop, long startFrame)
        {
            long freq = Stopwatch.Frequency;
            long startTicks = Stopwatch.GetTimestamp();
            long frame = startFrame;
            while (!stop.IsSet)
            {
                RenderTick(frame);

                // Publish the clock before pacing: if stop arrives during
                // the wait below, a later restart must resume after `frame`,
                // never re-render it (sink frame indices are strictly
                // increasing across stop/start cycles).
                long next = frame + 1;
                Interlocked.Exchange(ref _nextFrame, next);

                long due = startTicks + (next - startFrame) * freq / TargetFps;
                long now = Stopwatch.GetTimestamp();
                if (now < due)
                {
                    int waitMs = (int)((due - now) * 1000 / freq);
                    if (waitMs > 0 && stop.Wait(waitMs))
                    {
                        break;
                    }
                    // Sub-millisecond remainder (or an early timer wake-up):
                    // paint marginally early. Deadlines are recomputed from
                    // the absolute start each tick, so nothing accumulates.
                }
                else
                {
                    // Fell behind: skip the missed tick indices so animation
                    // stays at wall-clock 60 fps instead of slowing down.
                    long dueFrames = startFrame + (now - startTicks) * TargetFps / freq;
                    if (dueFrames > next)
                    {
                        next = dueFrames;
                        Interlocked.Exchange(ref _nextFrame, next);
                    }
                }
                frame = next;
            }
        }

        private void RenderTick(long frameIndex)
        {
            RenderInputMode mode;
            SignalState state;
            lock (_inputGate)
            {
                mode = _mode;
                state = _state;
            }

            uint frame = unchecked((uint)frameIndex);
            if (mode == RenderInputMode.ConnectedIdle)
            {
                // No game: signals cannot be active. Reset the envelope so a
                // session opening straight into a flag replays its onset.
                _envelope.Reset();
                Idles.PaintConnectedIdle(_back, frame);
            }
            else
            {
                // Blank mode maps to connected: false — the compositor
                // renders the boot-dark panel and the envelope resets.
                Composition comp = Compositor.Select(state, mode == RenderInputMode.Live);
                Envelopes env = _envelope.Update(comp, state, frame);
                Painter.Paint(_back, comp, env, state, frame);
            }

            byte[] pixels = _back.Pixels;
            IFrameSink[] sinks = _sinkSnapshot;
            for (int i = 0; i < sinks.Length; i++)
            {
                try
                {
                    sinks[i].OnFrame(pixels, frameIndex);
                }
                catch (Exception ex)
                {
                    RaiseSinkFaulted(sinks[i], ex);
                }
            }
        }

        private void RaiseSinkFaulted(IFrameSink sink, Exception ex)
        {
            Action<IFrameSink, Exception> handler = SinkFaulted;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(sink, ex);
            }
            catch
            {
                // A faulty observer must not kill the loop either.
            }
        }
    }
}
