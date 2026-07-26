// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The Grammar envelope — docs/flag-grammar.md §4: flash → attention window →
// ambient, with fade-out on clear. The tracker diffs successive
// (SignalState, Composition) pairs; epochs are keyed by signal CONDITION,
// not slot visibility, so:
//
//   - a signal re-emerging from behind a takeover or a higher-precedence
//     sibling resumes its own phase (no spurious flash — e.g. the incident
//     frame after a furled window, or the SC board after a red flag);
//   - suppression never destroys envelope progress;
//   - escalations (tier up, detail gained, board value change) re-arm the
//     window; de-escalations do not.
//
// Fade-out fires only when a condition actually CLEARS while its slot is
// empty; a replacement shows the arriving signal's flash instead. Painters
// should skip board/frame fade-outs under a takeover field (the takeover's
// own onset covers them).
//
// Not thread-safe; call Update exactly once per rendered frame from the
// render thread, with a monotonically incrementing (wrapping) frame counter.

namespace Uniflag.Rendering.Grammar
{
    /// <summary>Envelope phase for one slot (docs/flag-grammar.md §4).</summary>
    public enum EnvelopePhase
    {
        Hidden,
        Flash,
        Attention,
        Ambient,
        FadeOut,
    }

    /// <summary>One slot's envelope for the current frame.</summary>
    public struct SlotEnvelope
    {
        public EnvelopePhase Phase;

        /// <summary>
        /// Frames since the signal's epoch (Flash/Attention/Ambient) or since
        /// the fade started (FadeOut). Hidden: 0.
        /// </summary>
        public uint Age;

        /// <summary>FadeOut only: the departed kind, as the slot's enum value.</summary>
        public byte PriorKind;

        /// <summary>FadeOut only (board slot): the departed board's value payload.</summary>
        public byte PriorValue;
    }

    /// <summary>Envelopes for all three slots.</summary>
    public struct Envelopes
    {
        public SlotEnvelope Field;
        public SlotEnvelope Board;
        public SlotEnvelope Frame;
    }

    public sealed class EnvelopeTracker
    {
        /// <summary>Onset flash length (2 white + 6 blend frames).</summary>
        public const uint FlashFrames = 8;

        /// <summary>Attention window from signal entry, flash included (5 s).</summary>
        public const uint WindowFrames = 300;

        /// <summary>Fade-out length on clear (250 ms).</summary>
        public const uint FadeFrames = 15;

        private const int FieldKinds = 10;
        private const int BoardKinds = 7;
        private const int FrameKinds = 3;

        // The _want* scratch arrays are shared by the field and board
        // reconcilers, so they must span the larger of the two kind-spaces.
        private const int ScratchKinds = FieldKinds;

        private struct Cond
        {
            public bool Active;
            public uint Epoch;
            public byte DetailA;
            public uint DetailB;
        }

        private struct FadeState
        {
            public byte LastKind;
            public byte LastValue;
            public bool Fading;
            public uint FadeStart;
            public byte FadeKind;
            public byte FadeValue;
        }

        private readonly Cond[] _field = new Cond[FieldKinds];
        private readonly Cond[] _board = new Cond[BoardKinds];
        private readonly Cond[] _frame = new Cond[FrameKinds];

        private readonly bool[] _want = new bool[ScratchKinds];
        private readonly byte[] _wantA = new byte[ScratchKinds];
        private readonly uint[] _wantB = new uint[ScratchKinds];

        private FadeState _fieldFade;
        private FadeState _boardFade;
        private FadeState _frameFade;

        public void Reset()
        {
            System.Array.Clear(_field, 0, _field.Length);
            System.Array.Clear(_board, 0, _board.Length);
            System.Array.Clear(_frame, 0, _frame.Length);
            _fieldFade = default;
            _boardFade = default;
            _frameFade = default;
        }

        public Envelopes Update(in Composition comp, in SignalState s, uint frame)
        {
            ReconcileField(s, frame);
            ReconcileBoard(s, comp.Field, frame);
            ReconcileFrame(s, frame);

            return new Envelopes
            {
                Field = ComputeSlot(_field, (byte)comp.Field, 0, ref _fieldFade, frame),
                Board = ComputeSlot(_board, (byte)comp.Board, comp.BoardValue, ref _boardFade, frame),
                Frame = ComputeSlot(_frame, (byte)comp.Frame, 0, ref _frameFade, frame),
            };
        }

        private void ReconcileField(in SignalState s, uint frame)
        {
            for (int i = 0; i < FieldKinds; i++)
            {
                _want[i] = false;
                _wantA[i] = 0;
                _wantB[i] = 0;
            }

            FieldKind track = TrackFieldKind(s.Flag);
            if (track != FieldKind.None)
            {
                _want[(int)track] = true;
                _wantA[(int)track] = track == FieldKind.Red ? (byte)Tier.Urgent : (byte)s.Tier;
            }
            if (s.SafetyCar && !_want[(int)FieldKind.Yellow])
            {
                // Caution-forced yellow field (§5) is its own condition entry.
                _want[(int)FieldKind.Yellow] = true;
                _wantA[(int)FieldKind.Yellow] = (byte)Tier.Alert;
            }
            if (s.BlackActive)
            {
                _want[(int)FieldKind.Black] = true;
                _wantA[(int)FieldKind.Black] = (byte)Tier.Alert;
                // Bare black -> DQ is an escalation: the detail gained.
                _wantB[(int)FieldKind.Black] = s.Disqualified ? 1u : 0u;
            }
            if (s.Meatball)
            {
                _want[(int)FieldKind.Meatball] = true;
                _wantA[(int)FieldKind.Meatball] = (byte)Tier.Alert;
            }

            for (int i = 1; i < FieldKinds; i++)
            {
                if (!_want[i])
                {
                    _field[i].Active = false;
                    continue;
                }
                if (!_field[i].Active)
                {
                    _field[i] = new Cond { Active = true, Epoch = frame, DetailA = _wantA[i], DetailB = _wantB[i] };
                    continue;
                }
                // Escalation only (§4): the tier rose, or a detail bit was
                // gained. A detail that recedes rides the running window out.
                bool escalated = _wantA[i] > _field[i].DetailA
                    || (_wantB[i] & ~_field[i].DetailB) != 0;
                if (escalated)
                {
                    _field[i].Epoch = frame;
                }
                _field[i].DetailA = _wantA[i];
                _field[i].DetailB = _wantB[i];
            }
        }

        private void ReconcileBoard(in SignalState s, FieldKind field, uint frame)
        {
            for (int i = 0; i < BoardKinds; i++)
            {
                _want[i] = false;
                _wantA[i] = 0;
                _wantB[i] = 0;
            }

            if (s.SafetyCar) { _want[(int)BoardKind.SafetyCar] = true; }

            if (s.Disqualified) { _want[(int)BoardKind.Disqualified] = true; }

            if (s.BlackActive && field != FieldKind.Black && !s.Disqualified)
            {
                _want[(int)BoardKind.BlackFlag] = true;
            }
            if (s.Meatball && field != FieldKind.Meatball)
            {
                _want[(int)BoardKind.MeatballFlag] = true;
            }
            if (s.CountdownLaps > 0)
            {
                _want[(int)BoardKind.Countdown] = true;
                _wantB[(int)BoardKind.Countdown] = s.CountdownLaps;
            }
            if (s.StartPhase != StartPhase.Off)
            {
                _want[(int)BoardKind.StartGantry] = true;
                _wantA[(int)BoardKind.StartGantry] = (byte)s.StartPhase;
            }

            for (int i = 1; i < BoardKinds; i++)
            {
                if (!_want[i])
                {
                    _board[i].Active = false;
                    continue;
                }
                if (!_board[i].Active)
                {
                    _board[i] = new Cond { Active = true, Epoch = frame, DetailA = _wantA[i], DetailB = _wantB[i] };
                    continue;
                }
                if (_wantA[i] != _board[i].DetailA || _wantB[i] != _board[i].DetailB)
                {
                    _board[i].Epoch = frame;
                    _board[i].DetailA = _wantA[i];
                    _board[i].DetailB = _wantB[i];
                }
            }
        }

        private void ReconcileFrame(in SignalState s, uint frame)
        {
            ReconcileSimple(_frame, (int)FrameKind.Furled, s.Furled, frame);
            ReconcileSimple(_frame, (int)FrameKind.Incident, s.IncidentWarning, frame);
        }

        private static void ReconcileSimple(Cond[] conds, int kind, bool active, uint frame)
        {
            if (!active)
            {
                conds[kind].Active = false;
                return;
            }
            if (!conds[kind].Active)
            {
                conds[kind] = new Cond { Active = true, Epoch = frame };
            }
        }

        private static SlotEnvelope ComputeSlot(Cond[] conds, byte visibleKind, byte visibleValue, ref FadeState fade, uint frame)
        {
            if (visibleKind != 0)
            {
                fade.Fading = false;
                fade.LastKind = visibleKind;
                fade.LastValue = visibleValue;
                uint age = unchecked(frame - conds[visibleKind].Epoch);
                EnvelopePhase phase = age < FlashFrames ? EnvelopePhase.Flash
                    : age < WindowFrames ? EnvelopePhase.Attention
                    : EnvelopePhase.Ambient;
                return new SlotEnvelope { Phase = phase, Age = age };
            }

            if (fade.LastKind != 0)
            {
                if (conds[fade.LastKind].Active)
                {
                    // Suppressed, not cleared (e.g. a takeover): hard hide;
                    // the condition keeps its epoch for later re-emergence.
                    fade.LastKind = 0;
                    fade.Fading = false;
                    return default;
                }
                fade.Fading = true;
                fade.FadeStart = frame;
                fade.FadeKind = fade.LastKind;
                fade.FadeValue = fade.LastValue;
                fade.LastKind = 0;
            }

            if (fade.Fading)
            {
                uint fadeAge = unchecked(frame - fade.FadeStart);
                if (fadeAge < FadeFrames)
                {
                    return new SlotEnvelope
                    {
                        Phase = EnvelopePhase.FadeOut,
                        Age = fadeAge,
                        PriorKind = fade.FadeKind,
                        PriorValue = fade.FadeValue,
                    };
                }
                fade.Fading = false;
            }

            return default;
        }

        private static FieldKind TrackFieldKind(TrackFlag flag)
        {
            switch (flag)
            {
                case TrackFlag.Yellow:
                    return FieldKind.Yellow;
                case TrackFlag.Blue:
                    return FieldKind.Blue;
                case TrackFlag.White:
                    return FieldKind.White;
                case TrackFlag.Red:
                    return FieldKind.Red;
                case TrackFlag.Green:
                    return FieldKind.Green;
                case TrackFlag.Checkered:
                    return FieldKind.Checkered;
                case TrackFlag.Debris:
                    return FieldKind.Debris;
                default:
                    return FieldKind.None;
            }
        }
    }
}
