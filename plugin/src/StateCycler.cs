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

using System;
using System.Collections.Generic;
using System.Threading;
using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;
using Caution = Uniflag.Rendering.Grammar.Caution;

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
        /// The deterministic tour over the full Grammar vocabulary — every
        /// field at its tiers, the black-flag family with demotions, the
        /// regime boards, sector strips, notice boards, the gantry phases,
        /// the frame advisories and both idles. Pure — two calls yield
        /// identical sequences.
        /// </summary>
        public static IReadOnlyList<SignalState> BuildSequence()
        {
            return new List<SignalState>
            {
                // Idles: violet session flankers, then the static race ticks.
                Make(session: Session.PreRace),
                Make(),

                // Track-state fields through the tier ladder.
                Make(flag: TrackFlag.Yellow, tier: Tier.Ambient),
                Make(flag: TrackFlag.Yellow, tier: Tier.Alert),
                Make(flag: TrackFlag.Yellow, tier: Tier.Urgent),
                Make(flag: TrackFlag.Blue, tier: Tier.Ambient),
                Make(flag: TrackFlag.Blue, tier: Tier.Alert),
                Make(flag: TrackFlag.White),
                Make(flag: TrackFlag.Green, tier: Tier.Alert),
                Make(flag: TrackFlag.Red),
                Make(flag: TrackFlag.Checkered),
                Make(flag: TrackFlag.Debris),

                // The black-flag family, its details, and the demotions.
                Make(blackFlag: true),
                Make(blackFlag: true, blackDetail: BlackDetail.DriveThrough),
                Make(blackFlag: true, blackDetail: BlackDetail.StopAndGo),
                Make(blackDetail: BlackDetail.Disqualified),
                Make(meatball: true),
                Make(flag: TrackFlag.Yellow, tier: Tier.Alert, blackFlag: true),
                Make(blackFlag: true, meatball: true),

                // Neutralisation regimes: yellow field + board.
                Make(caution: Caution.SafetyCar),
                Make(caution: Caution.VirtualSafetyCar),
                Make(caution: Caution.FullCourseYellow),

                // Sector strips under a local yellow.
                Make(flag: TrackFlag.Yellow, tier: Tier.Alert, sectors: SectorSet.FromBits(0b001)),
                Make(flag: TrackFlag.Yellow, tier: Tier.Alert, sectors: SectorSet.FromBits(0b101)),
                Make(flag: TrackFlag.Yellow, tier: Tier.Urgent, sectors: SectorSet.FromBits(0b111)),

                // Notice boards.
                Make(timePenaltySeconds: 5),
                Make(countdownLaps: 10),
                Make(countdownLaps: 5),

                // Start-sequence gantry.
                Make(startPhase: StartPhase.Ready),
                Make(startPhase: StartPhase.Set),
                Make(startPhase: StartPhase.Set, startLightsLit: 3),
                Make(startPhase: StartPhase.Go),

                // Frame advisories, alone and riding a field.
                Make(furled: true),
                Make(incidentWarning: true),
                Make(flag: TrackFlag.Yellow, tier: Tier.Alert, incidentWarning: true),
            };
        }

        private static SignalState Make(
            TrackFlag flag = TrackFlag.None,
            Tier tier = Tier.Ambient,
            bool blackFlag = false,
            BlackDetail blackDetail = BlackDetail.None,
            bool meatball = false,
            Session session = Session.Racing,
            Caution caution = Caution.None,
            SectorSet sectors = default,
            StartPhase startPhase = StartPhase.Off,
            byte startLightsLit = 0,
            byte timePenaltySeconds = 0,
            byte countdownLaps = 0,
            bool furled = false,
            bool incidentWarning = false)
        {
            return new SignalState
            {
                Flag = flag,
                Tier = tier,
                BlackFlag = blackFlag,
                BlackDetail = blackDetail,
                Meatball = meatball,
                Session = session,
                Caution = caution,
                Sectors = sectors,
                StartPhase = startPhase,
                StartLightsLit = startLightsLit,
                TimePenaltySeconds = timePenaltySeconds,
                CountdownLaps = countdownLaps,
                Furled = furled,
                IncidentWarning = incidentWarning,
            };
        }
    }
}
