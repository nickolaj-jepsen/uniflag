// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Debug state-cycler (docs/v2-plan.md M3 step 7): steps the renderer's
// input through a deterministic tour of the full flag/wave/caution/sector
// vocabulary so the preview animates with zero hardware and zero game.
// Deliberately WPF-free (it only talks to the rendering core) so the
// sequence and stepping logic are unit-testable from plain xunit.

using System;
using System.Collections.Generic;
using System.Threading;
using Uniflag.Rendering;

namespace Uniflag
{
    /// <summary>
    /// Steps a <see cref="RendererLoop"/>'s input state through
    /// <see cref="BuildSequence"/> every <see cref="StepMilliseconds"/>,
    /// always with <c>connected = true</c>. <see cref="Stop"/> parks the
    /// renderer back on the blank disconnected default (the firmware's
    /// boot-dark posture), so disabling the cycler never leaves a stale
    /// flag frozen on the preview.
    ///
    /// Thread-safe; <see cref="Start"/>/<see cref="Stop"/> are idempotent
    /// and restart the tour from the beginning.
    /// </summary>
    public sealed class StateCycler : IDisposable
    {
        /// <summary>Dwell time per state — long enough to see every strobe/sweep.</summary>
        public const int StepMilliseconds = 2000;

        private readonly Action<RenderState, bool> _apply;
        private readonly IReadOnlyList<RenderState> _sequence;
        private readonly object _gate = new object();
        private Timer _timer;
        private int _index;

        /// <summary>Cycle <paramref name="renderer"/>'s input state.</summary>
        public StateCycler(RendererLoop renderer)
            : this(MakeApply(renderer))
        {
        }

        /// <summary>
        /// Core constructor: <paramref name="apply"/> receives each
        /// (state, connected) step. Used directly by tests to observe the
        /// applied sequence without a renderer.
        /// </summary>
        public StateCycler(Action<RenderState, bool> apply)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _sequence = BuildSequence();
        }

        private static Action<RenderState, bool> MakeApply(RendererLoop renderer)
        {
            if (renderer == null)
            {
                throw new ArgumentNullException(nameof(renderer));
            }
            return renderer.SetState;
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
        /// Stop stepping, wait for any in-flight step to finish, then park
        /// the renderer input on the blank disconnected default. No-op if
        /// not running.
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
                // callbacks complete. Even if the 1 s wait ever expired,
                // OnTimer's gate below makes a straggler a no-op — the
                // parked default cannot be overwritten either way.
                if (timer.Dispose(drained))
                {
                    drained.WaitOne(1000);
                }
            }
            lock (_gate)
            {
                // Skip the park if a racing Start already began a new tour.
                if (_timer == null)
                {
                    _apply(RenderState.Default, false);
                }
            }
        }

        /// <summary>
        /// Apply the next state in the tour (wrapping at the end). Called by
        /// the timer; public so tests and diagnostics can single-step
        /// without waiting on wall-clock time.
        /// </summary>
        public void Advance()
        {
            lock (_gate)
            {
                RenderState next = _sequence[_index];
                _index = (_index + 1) % _sequence.Count;
                _apply(next, true);
            }
        }

        public void Dispose() => Stop();

        private void OnTimer(object unused)
        {
            lock (_gate)
            {
                // A callback that lost the race with Stop must not step (or
                // clobber the parked default); manual Advance stays un-gated
                // for tests/diagnostics.
                if (_timer == null)
                {
                    return;
                }
                RenderState next = _sequence[_index];
                _index = (_index + 1) % _sequence.Count;
                _apply(next, true);
            }
        }

        /// <summary>
        /// The deterministic tour: both no-flag idle layers (ready orb,
        /// race idle), every flag at every wave level, both caution boards,
        /// every non-empty sector mask over the race-idle base, and one
        /// double-waved yellow with all sectors (the 4 Hz band over a
        /// strobing base). Pure — two calls yield identical sequences.
        /// </summary>
        public static IReadOnlyList<RenderState> BuildSequence()
        {
            var sequence = new List<RenderState>
            {
                // The two session-idle layers (docs/effects-spec.md §5.10/§5.11).
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

            // Both caution boards (VSC, SC).
            sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.VirtualSafetyCar, SectorSet.Empty));
            sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.SafetyCar, SectorSet.Empty));

            // Every non-empty sector mask, banded over the race-idle base.
            for (byte bits = 1; bits <= 7; bits++)
            {
                sequence.Add(Make(Flag.None, WaveLevel.None, Session.Racing, Caution.None, SectorSet.FromBits(bits)));
            }

            // 4 Hz sector band over a double-waved yellow strobe.
            sequence.Add(Make(Flag.Yellow, WaveLevel.Double, Session.Racing, Caution.None, SectorSet.FromBits(0b111)));

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
    }
}
