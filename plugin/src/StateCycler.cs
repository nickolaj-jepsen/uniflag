// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Debug state-cycler: steps the renderer's OVERRIDE input through a
// deterministic tour of the full flag/wave/caution/sector vocabulary so the
// preview animates with zero hardware and zero game — and keeps animating
// even while DataUpdate feeds the normal input at 60 Hz. Deliberately
// WPF-free so the sequence and stepping logic are unit-testable from plain
// xunit.

using System;
using System.Collections.Generic;
using System.Threading;
using Uniflag.Rendering;

namespace Uniflag
{
    /// <summary>
    /// Steps a <see cref="RendererLoop"/>'s override input through
    /// <see cref="BuildSequence"/> every <see cref="StepMilliseconds"/>. The
    /// override channel wins over the live telemetry feed, so the tour shows
    /// even mid-session; <see cref="Stop"/> clears the override, falling back
    /// to the last normal input — never a stale tour flag frozen on the
    /// preview. Thread-safe; Start/Stop are idempotent and restart the tour
    /// from the beginning.
    /// </summary>
    public sealed class StateCycler : IDisposable
    {
        /// <summary>Dwell time per state — long enough to see every strobe/sweep.</summary>
        public const int StepMilliseconds = 2000;

        private readonly Action<RenderState, bool> _applyOverride;
        private readonly Action _clearOverride;
        private readonly IReadOnlyList<RenderState> _sequence;
        private readonly object _gate = new object();
        private Timer _timer;
        private int _index;

        /// <summary>Cycle <paramref name="renderer"/>'s override input.</summary>
        public StateCycler(RendererLoop renderer)
            : this(OverrideOf(renderer), renderer.ClearOverride)
        {
        }

        /// <summary>
        /// Core constructor. Used directly by tests to observe the applied
        /// sequence without a renderer.
        /// </summary>
        public StateCycler(Action<RenderState, bool> applyOverride, Action clearOverride)
        {
            _applyOverride = applyOverride ?? throw new ArgumentNullException(nameof(applyOverride));
            _clearOverride = clearOverride ?? throw new ArgumentNullException(nameof(clearOverride));
            _sequence = BuildSequence();
        }

        private static Action<RenderState, bool> OverrideOf(RendererLoop renderer)
        {
            if (renderer == null)
            {
                throw new ArgumentNullException(nameof(renderer));
            }
            return renderer.SetOverrideState;
        }

        /// <summary>Whether the timer is currently stepping.</summary>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _timer != null;
                }
            }
        }

        /// <summary>
        /// Begin the tour: the first state is applied (almost) immediately,
        /// then one step every <see cref="StepMilliseconds"/>. No-op if
        /// already running.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_timer != null)
                {
                    return;
                }
                _index = 0;
                _timer = new Timer(OnTimer, null, 0, StepMilliseconds);
            }
        }

        /// <summary>
        /// Stop stepping, wait for any in-flight step to finish, then clear
        /// the renderer's override so it falls back to the normal input.
        /// No-op if not running.
        /// </summary>
        public void Stop()
        {
            Timer timer;
            lock (_gate)
            {
                timer = _timer;
                _timer = null;
            }
            if (timer == null)
            {
                return;
            }
            using (var drained = new ManualResetEvent(false))
            {
                // Dispose(WaitHandle) signals only after queued/executing
                // callbacks complete. Even if the 1 s wait expired, OnTimer's
                // gate makes a straggler a no-op — a cleared override cannot
                // be re-set by a stale step.
                if (timer.Dispose(drained))
                {
                    drained.WaitOne(1000);
                }
            }
            lock (_gate)
            {
                // Skip the clear if a racing Start already began a new tour.
                if (_timer == null)
                {
                    _clearOverride();
                }
            }
        }

        /// <summary>
        /// Apply the next state in the tour (wrapping at the end). Public so
        /// tests and diagnostics can single-step without wall-clock time.
        /// </summary>
        public void Advance()
        {
            lock (_gate)
            {
                RenderState next = _sequence[_index];
                _index = (_index + 1) % _sequence.Count;
                _applyOverride(next, true);
            }
        }

        public void Dispose() => Stop();

        private void OnTimer(object unused)
        {
            lock (_gate)
            {
                // A callback that lost the race with Stop must not step (or
                // re-assert a cleared override); manual Advance stays
                // un-gated for tests/diagnostics.
                if (_timer == null)
                {
                    return;
                }
                RenderState next = _sequence[_index];
                _index = (_index + 1) % _sequence.Count;
                _applyOverride(next, true);
            }
        }

        /// <summary>
        /// The deterministic tour over the full state vocabulary. Pure — two
        /// calls yield identical sequences.
        /// </summary>
        public static IReadOnlyList<RenderState> BuildSequence()
        {
            var sequence = new List<RenderState>
            {
                Make(Flag.None, WaveLevel.None, Session.PreRace, Caution.None, SectorSet.Empty),
                Make(Flag.None, WaveLevel.None, Session.Racing, Caution.None, SectorSet.Empty),
            };

            // Every flag at every wave level. Order puts a non-red flag
            // before red and a non-green before green, so both onset
            // animations (flag-age resets) are visible each lap of the tour.
            Flag[] flags =
            {
                Flag.Yellow, Flag.Blue, Flag.Black, Flag.White,
                Flag.Red, Flag.Green, Flag.Checkered, Flag.Orange,
            };
            WaveLevel[] waves = { WaveLevel.None, WaveLevel.Single, WaveLevel.Double };
            foreach (Flag flag in flags)
            {
                foreach (WaveLevel wave in waves)
                {
                    sequence.Add(Make(flag, wave, Session.Racing, Caution.None, SectorSet.Empty));
                }
            }

            sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.VirtualSafetyCar, SectorSet.Empty));
            sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.SafetyCar, SectorSet.Empty));

            for (byte bits = 1; bits <= 7; bits++)
            {
                sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.None, SectorSet.FromBits(bits)));
            }

            // 4 Hz sector band over a double-waved yellow strobe.
            sequence.Add(Make(Flag.Yellow, WaveLevel.Double, Session.Racing, Caution.None, SectorSet.FromBits(0b111)));

            // Penalty suite — the WPF-preview review path for the C#-authored
            // golden corpus.
            sequence.Add(MakePenalty(slowdown: 1));
            sequence.Add(MakePenalty(slowdown: 2));
            sequence.Add(MakePenalty(slowdown: 3));
            sequence.Add(MakePenalty(meatball: true));
            sequence.Add(MakePenalty(flag: Flag.Black, blackDetail: BlackFlagDetail.DriveThrough));
            sequence.Add(MakePenalty(flag: Flag.Black, blackDetail: BlackFlagDetail.StopAndGo));
            sequence.Add(MakePenalty(furled: true));
            sequence.Add(MakePenalty(flag: Flag.Yellow, furled: true));

            return sequence;
        }

        private static RenderState Make(
            Flag flag, WaveLevel wave, Session session, Caution caution, SectorSet sectors)
        {
            return new RenderState
            {
                Flag = flag,
                Wave = wave,
                Session = session,
                Caution = caution,
                Sectors = sectors,
            };
        }

        /// <summary>
        /// A penalty-suite tour step: <see cref="Session.Racing"/> base with
        /// the given penalty dimensions.
        /// </summary>
        private static RenderState MakePenalty(
            Flag flag = Flag.None,
            byte slowdown = 0,
            bool meatball = false,
            BlackFlagDetail blackDetail = BlackFlagDetail.None,
            bool furled = false)
        {
            RenderState state = Make(flag, WaveLevel.None, Session.Racing, Caution.None, SectorSet.Empty);
            state.Slowdown = slowdown;
            state.Meatball = meatball;
            state.BlackDetail = blackDetail;
            state.Furled = furled;
            return state;
        }
    }
}
