// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Debug state-cycler: steps the renderer's OVERRIDE input through a
// deterministic tour of the full Grammar signal vocabulary
// (docs/flag-grammar.md §6-§7) so the preview animates with zero hardware
// and zero game — and keeps animating even while DataUpdate feeds the
// normal input at 60 Hz. Each 2 s dwell shows a signal's onset flash and
// attention-window motion (the envelope re-arms on every state change).
// Deliberately WPF-free so the sequence and stepping logic are
// unit-testable from plain xunit.
//
// The tour is derived from ScenarioCatalogue so the vocabulary is
// enumerated once, not once per consumer.

using System;
using System.Collections.Generic;
using System.Threading;
using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;

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
        /// <summary>Dwell time per state — long enough to see the onset flash and the tier motion.</summary>
        public const int StepMilliseconds = 2000;

        private readonly Action<SignalState, bool> _applyOverride;
        private readonly Action _clearOverride;
        private readonly IReadOnlyList<SignalState> _sequence;
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
        public StateCycler(Action<SignalState, bool> applyOverride, Action clearOverride)
        {
            _applyOverride = applyOverride ?? throw new ArgumentNullException(nameof(applyOverride));
            _clearOverride = clearOverride ?? throw new ArgumentNullException(nameof(clearOverride));
            _sequence = BuildSequence();
        }

        private static Action<SignalState, bool> OverrideOf(RendererLoop renderer)
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
                SignalState next = _sequence[_index];
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
                SignalState next = _sequence[_index];
                _index = (_index + 1) % _sequence.Count;
                _applyOverride(next, true);
            }
        }

        /// <summary>
        /// The deterministic tour: each scenario's state at its sample frame.
        /// Consecutive duplicates are dropped — scenarios that differ only in
        /// which frame they sample (onset flash vs. settled) are distinct
        /// pictures but the same input, and dwelling twice reads as a stall.
        /// Pure.
        /// </summary>
        public static IReadOnlyList<SignalState> BuildSequence()
        {
            var sequence = new List<SignalState>();
            foreach (ScenarioCatalogue.Scenario scenario in ScenarioCatalogue.Table)
            {
                SignalState state = scenario.StateAt(scenario.SampleFrame);
                if (sequence.Count == 0 || !SameState(sequence[sequence.Count - 1], state))
                {
                    sequence.Add(state);
                }
            }
            return sequence;
        }

        /// <summary>
        /// Field-wise: the default <c>ValueType.Equals</c> would box and
        /// reflect on every comparison.
        /// </summary>
        private static bool SameState(in SignalState a, in SignalState b)
        {
            return a.Flag == b.Flag
                && a.Tier == b.Tier
                && a.BlackFlag == b.BlackFlag
                && a.BlackDetail == b.BlackDetail
                && a.Meatball == b.Meatball
                && a.Session == b.Session
                && a.Caution == b.Caution
                && a.Sectors.Equals(b.Sectors)
                && a.StartPhase == b.StartPhase
                && a.StartLightsLit == b.StartLightsLit
                && a.TimePenaltySeconds == b.TimePenaltySeconds
                && a.CountdownLaps == b.CountdownLaps
                && a.Furled == b.Furled
                && a.IncidentWarning == b.IncidentWarning;
        }
    }
}
