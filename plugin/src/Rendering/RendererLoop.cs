// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The one-renderer→N-sinks core: a dedicated background thread ticks the
// 60 fps internal frame counter that all animation math assumes
// (docs/effects-spec.md §1), paints the whole panel into a back buffer,
// publishes the completed 3072-byte RGB888 frame, and hands it to every
// registered IFrameSink. Sinks sample this clock; the counter is never
// rebased to a sink's rate (30 fps USB, the overlay's measured fps, WPF's
// display cadence) — that would halve every strobe rate.
//
// WPF-free by contract: plugin/src/Rendering/ must stay loadable from plain
// xunit with no SimHub-assembly or System.Windows dependency.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Uniflag.Rendering
{
    /// <summary>
    /// What the renderer paints each tick — the three-state idle model of
    /// docs/effects-spec.md §7, from the plugin's point of view.
    /// </summary>
    public enum RenderInputMode
    {
        /// <summary>
        /// Boot-dark blank panel (the firmware's disconnected posture,
        /// §7 state (a) as seen host-side). The initial mode.
        /// </summary>
        Blank,

        /// <summary>
        /// Plugin connected-idle (§7b): renderer alive, no game session.
        /// Painted by <see cref="IdleEffects.PaintConnectedIdle"/> — never
        /// by <see cref="Effects.Paint"/>, which stays golden-frozen.
        /// </summary>
        ConnectedIdle,

        /// <summary>Live game state through <see cref="Effects.Paint"/>.</summary>
        Live,
    }

    /// <summary>
    /// The renderer loop. Lifecycle is ref-counted on sink registration: the
    /// render thread runs while at least one sink is registered and stops
    /// when the last one is removed (<see cref="Dispose"/> stops it
    /// unconditionally). The animation clock (frame index, flag age) is
    /// preserved across stop/start cycles.
    ///
    /// <para><b>Input arbitration:</b> two channels feed the loop. The
    /// <i>normal</i> channel (<see cref="SetState"/>,
    /// <see cref="SetConnectedIdle"/>) is for the live telemetry path; the
    /// <i>override</i> channel (<see cref="SetOverrideState"/>,
    /// <see cref="ClearOverride"/>) is for diagnostics like the settings-tab
    /// state cycler. While an override is active it wins every tick — a
    /// 60 Hz DataUpdate stream cannot clobber it; clearing it falls back to
    /// whatever the normal channel last said (boot-dark blank if it never
    /// spoke).</para>
    ///
    /// <para><b>Hot path:</b> zero allocation per frame — the paint target,
    /// the publish buffer and the sink snapshot array are all reused;
    /// allocations happen only on sink add/remove and thread start.</para>
    ///
    /// <para><b>Pacing:</b> monotonic <see cref="Stopwatch"/> deadlines —
    /// frame <c>n</c> is due at <c>start + n/60 s</c>, recomputed from the
    /// absolute start each tick so timer slop never accumulates into drift.
    /// If the thread falls behind (GC pause, slow sink, debugger), it skips
    /// the missed tick indices instead of slewing the clock: sinks see fewer
    /// frames, but animation rates stay wall-clock true.</para>
    /// </summary>
    public sealed class RendererLoop : IDisposable
    {
        /// <summary>
        /// The internal animation tick rate. Fixed by the ported effects
        /// math (docs/effects-spec.md §1) — never derive it from a sink.
        /// </summary>
        public const int TargetFps = 60;

        // _sinkSnapshot is copy-on-write so the render thread reads it without locking.
        private readonly object _gate = new object();
        private readonly List<IFrameSink> _sinks = new List<IFrameSink>();
        private volatile IFrameSink[] _sinkSnapshot = Array.Empty<IFrameSink>();
        private Thread _thread;
        private ManualResetEventSlim _stop;
        // Last stopped render thread, possibly still draining its final tick
        // (its join happens outside _gate). StartLocked waits for it so two
        // render threads can never share _back / deliver duplicate indices.
        private Thread _retiring;
        private bool _disposed;

        // Latest input, latched once at the top of every tick. The normal
        // channel boots blank (firmware's boot-dark posture) until someone
        // feeds it; the override channel, when active, shadows it entirely.
        private readonly object _inputGate = new object();
        private RenderInputMode _normalMode = RenderInputMode.Blank;
        private RenderState _normalState = RenderState.Default;
        private bool _overrideActive;
        private RenderInputMode _overrideMode = RenderInputMode.Blank;
        private RenderState _overrideState = RenderState.Default;

        // Double buffer: _back is painted each tick; _front is the published
        // copy handed to sinks and pull-readers, published under _publishGate
        // so no consumer ever observes a partially painted frame.
        private readonly FrameBuffer _back = new FrameBuffer();
        private readonly byte[] _front = new byte[FrameBuffer.ByteLength];
        private readonly object _publishGate = new object();
        private long _publishedIndex = -1;

        // Next frame index to render. Interlocked so a stop/start cycle
        // resumes the clock instead of restarting it.
        private long _nextFrame;

        // Render-thread-only flag-age bookkeeping: record the tick at which
        // the latched flag last changed; age is the wrapping u32 difference.
        // Handed across stop/start cycles by the Thread.Start/Join barriers.
        private Flag _lastFlag = RenderState.Default.Flag;
        private uint _flagChangedAtFrame;

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
        /// Replace the renderer's <b>normal</b> input. Thread-safe; the new
        /// value is latched at the start of the next tick (or shadowed until
        /// an active override clears). Flag-age tracking (red and green
        /// onsets, the ready orb's 5 s fallback) resets whenever the latched
        /// <see cref="RenderState.Flag"/> differs from the previous tick's —
        /// session or caution changes do not reset it.
        /// </summary>
        public void SetState(RenderState state, bool connected)
        {
            lock (_inputGate)
            {
                _normalMode = connected ? RenderInputMode.Live : RenderInputMode.Blank;
                _normalState = state;
            }
        }

        /// <summary>
        /// Switch the <b>normal</b> input to the connected-idle marker
        /// (docs/effects-spec.md §7b) — renderer alive, no game session.
        /// The latched state becomes <see cref="RenderState.Default"/>
        /// (flag None), so flag-age bookkeeping keeps the firmware's
        /// posture: a game session opening straight into a flag replays
        /// that flag's onset, while opening flagless does not restart the
        /// ready-orb window.
        /// </summary>
        public void SetConnectedIdle()
        {
            lock (_inputGate)
            {
                _normalMode = RenderInputMode.ConnectedIdle;
                _normalState = RenderState.Default;
            }
        }

        /// <summary>
        /// Activate (or update) the <b>override</b> input: it wins over the
        /// normal channel every tick until <see cref="ClearOverride"/>.
        /// Same (state, connected) semantics as <see cref="SetState"/>;
        /// flag-age tracking follows whichever channel is being painted.
        /// </summary>
        public void SetOverrideState(RenderState state, bool connected)
        {
            lock (_inputGate)
            {
                _overrideActive = true;
                _overrideMode = connected ? RenderInputMode.Live : RenderInputMode.Blank;
                _overrideState = state;
            }
        }

        /// <summary>
        /// Deactivate the override input, falling back to the last normal
        /// input (boot-dark blank if the normal channel was never fed).
        /// Idempotent.
        /// </summary>
        public void ClearOverride()
        {
            lock (_inputGate)
            {
                _overrideActive = false;
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
            Thread toJoin = null;
            lock (_gate)
            {
                if (!_sinks.Remove(sink))
                {
                    return;
                }
                _sinkSnapshot = _sinks.ToArray();
                if (_sinks.Count == 0)
                {
                    toJoin = StopLocked();
                }
            }
            JoinOutsideLock(toJoin);
        }

        /// <summary>
        /// Copy the latest completed frame into <paramref name="destination"/>
        /// (at least <see cref="FrameBuffer.ByteLength"/> bytes). Returns the
        /// frame's tick index, or -1 if nothing has been rendered yet.
        /// </summary>
        public long CopyLatestFrame(byte[] destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if (destination.Length < FrameBuffer.ByteLength)
            {
                throw new ArgumentException(
                    $"destination must hold at least {FrameBuffer.ByteLength} bytes", nameof(destination));
            }
            lock (_publishGate)
            {
                if (_publishedIndex < 0)
                {
                    return -1;
                }
                Buffer.BlockCopy(_front, 0, destination, 0, FrameBuffer.ByteLength);
                return _publishedIndex;
            }
        }

        /// <summary>
        /// Stop the render thread (regardless of registered sinks), drop all
        /// sinks, and make further <see cref="AddSink"/> calls no-ops.
        /// Idempotent.
        /// </summary>
        public void Dispose()
        {
            Thread toJoin;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _sinks.Clear();
                _sinkSnapshot = Array.Empty<IFrameSink>();
                toJoin = StopLocked();
            }
            JoinOutsideLock(toJoin);
        }

        // -------------------------------------------------------------------
        // Thread lifecycle (callers hold _gate).
        // -------------------------------------------------------------------

        private void StartLocked()
        {
            // Joining under _gate is safe: the render thread never takes
            // _gate (sink snapshots are copy-on-write). The re-entrant case
            // gets the same degrade-don't-hang guard as JoinOutsideLock;
            // joining an already-dead thread returns immediately.
            Thread retiring = _retiring;
            if (retiring != null && retiring != Thread.CurrentThread)
            {
                retiring.Join();
            }
            _retiring = null;

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

        private Thread StopLocked()
        {
            Thread thread = _thread;
            if (thread == null)
            {
                return null;
            }
            _stop.Set();
            // Deliberately not disposed: the render thread may still be inside
            // Wait(), and a signalled-then-collected ManualResetEventSlim costs
            // nothing measurable.
            _stop = null;
            _thread = null;
            _retiring = thread;
            return thread;
        }

        private static void JoinOutsideLock(Thread thread)
        {
            // Joining from the render thread itself would self-deadlock;
            // that only happens if a sink re-enters Add/RemoveSink, which
            // the IFrameSink contract forbids — degrade to a signal-only
            // stop rather than hanging.
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        // -------------------------------------------------------------------
        // Render thread.
        // -------------------------------------------------------------------

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
            RenderState state;
            lock (_inputGate)
            {
                if (_overrideActive)
                {
                    mode = _overrideMode;
                    state = _overrideState;
                }
                else
                {
                    mode = _normalMode;
                    state = _normalState;
                }
            }

            // Wrapping u32 frame counter + flag age. Tracking runs in every
            // mode — ConnectedIdle latches RenderState.Default (flag None),
            // keeping the last flag bookkeeping across disconnected spells.
            uint frame = unchecked((uint)frameIndex);
            if (state.Flag != _lastFlag)
            {
                _lastFlag = state.Flag;
                _flagChangedAtFrame = frame;
            }
            uint flagAge = unchecked(frame - _flagChangedAtFrame);

            if (mode == RenderInputMode.ConnectedIdle)
            {
                // Separate painter by contract: Effects.Paint is pinned by
                // the golden corpus and must not grow new code paths.
                IdleEffects.PaintConnectedIdle(_back, frame);
            }
            else
            {
                Effects.Paint(_back, state, frame, flagAge, mode == RenderInputMode.Live);
            }

            lock (_publishGate)
            {
                Buffer.BlockCopy(_back.Pixels, 0, _front, 0, FrameBuffer.ByteLength);
                _publishedIndex = frameIndex;
            }

            // Push the published frame to every sink. _front is stable until
            // the next tick's publish, and all sinks have returned by then —
            // the IFrameSink borrow rule covers anyone who copies out.
            IFrameSink[] sinks = _sinkSnapshot;
            for (int i = 0; i < sinks.Length; i++)
            {
                try
                {
                    sinks[i].OnFrame(_front, frameIndex);
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
