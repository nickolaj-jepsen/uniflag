// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The Grammar compositor — docs/flag-grammar.md §5. Pure dispatch, separate
// from the painters so slot selection is unit-testable in isolation (same
// posture as the legacy Precedence class). Three slots render concurrently:
// one field, at most one board, at most one frame accent, plus the sector
// strip painted last. Precedence works WITHIN slots; red and checkered are
// field-exclusive takeovers.

namespace Uniflag.Rendering.Grammar
{
    /// <summary>Field-slot content: full-panel cloth-flag renderings.</summary>
    public enum FieldKind
    {
        None,
        Yellow,
        Red,
        Green,
        Blue,
        White,
        Black,
        Meatball,
        Checkered,
        Debris,
    }

    /// <summary>Board-slot content, in precedence order (highest first after None).</summary>
    public enum BoardKind
    {
        None,
        SafetyCar,
        VirtualSafetyCar,
        FullCourseYellow,
        Disqualified,
        StopAndGo,
        DriveThrough,

        /// <summary>Demoted bare black flag: the X-glyph board (docs/flag-grammar.md §5).</summary>
        BlackFlag,

        /// <summary>Demoted meatball: the disc-icon board.</summary>
        MeatballFlag,

        TimePenalty,
        Countdown,
        StartGantry,
    }

    /// <summary>Frame-slot content: 1-px perimeter advisories.</summary>
    public enum FrameKind
    {
        None,
        Furled,
        Incident,
    }

    /// <summary>The three slots (plus strip visibility) selected for one tick.</summary>
    public struct Composition
    {
        /// <summary>False = host silent: the panel is blank (firmware fallback owns it).</summary>
        public bool Connected;

        public FieldKind Field;

        /// <summary>Urgency the field renders at (adapter tier, takeover- and caution-adjusted).</summary>
        public Tier FieldTier;

        public BoardKind Board;

        /// <summary>Payload for value-carrying boards: penalty seconds, countdown laps, gantry lights lit.</summary>
        public byte BoardValue;

        public FrameKind Frame;

        public bool SectorStrip;
    }

    /// <summary>Pure slot selection. No animation state — that lives in <see cref="EnvelopeTracker"/>.</summary>
    public static class Compositor
    {
        public static Composition Select(in SignalState s, bool connected)
        {
            if (!connected)
            {
                return default;
            }

            bool blackActive = s.BlackActive;

            // Field slot: Red > Yellow > Black > Meatball > (other track flag).
            FieldKind field;
            Tier tier = s.Tier;
            if (s.Flag == TrackFlag.Red)
            {
                field = FieldKind.Red;
                tier = Tier.Urgent;
            }
            else if (s.Flag == TrackFlag.Yellow)
            {
                field = FieldKind.Yellow;
            }
            else if (blackActive)
            {
                field = FieldKind.Black;
                tier = Tier.Alert;
            }
            else if (s.Meatball)
            {
                field = FieldKind.Meatball;
                tier = Tier.Alert;
            }
            else
            {
                field = TrackField(s.Flag);
            }

            // A neutralisation regime rides a yellow field (§5): the yielding
            // kinds give way; red/checkered takeovers and driver-directed
            // fields keep the slot (their boards/demotions carry the rest).
            if (s.Caution != Caution.None && Yields(field))
            {
                field = FieldKind.Yellow;
                tier = Tier.Alert;
            }

            bool takeover = field == FieldKind.Red || field == FieldKind.Checkered;
            bool disqualified = s.BlackDetail == BlackDetail.Disqualified;

            // Board slot (suppressed entirely by takeovers): regime > DQ > SG
            // > DT > demoted X > demoted disc > +N > countdown > gantry.
            BoardKind board = BoardKind.None;
            byte boardValue = 0;
            if (!takeover)
            {
                if (s.Caution == Caution.SafetyCar)
                {
                    board = BoardKind.SafetyCar;
                }
                else if (s.Caution == Caution.VirtualSafetyCar)
                {
                    board = BoardKind.VirtualSafetyCar;
                }
                else if (s.Caution == Caution.FullCourseYellow)
                {
                    board = BoardKind.FullCourseYellow;
                }
                else if (s.BlackDetail == BlackDetail.Disqualified)
                {
                    board = BoardKind.Disqualified;
                }
                else if (s.BlackDetail == BlackDetail.StopAndGo)
                {
                    board = BoardKind.StopAndGo;
                }
                else if (s.BlackDetail == BlackDetail.DriveThrough)
                {
                    board = BoardKind.DriveThrough;
                }
                else if (blackActive && field != FieldKind.Black)
                {
                    board = BoardKind.BlackFlag;
                }
                else if (s.Meatball && field != FieldKind.Meatball)
                {
                    board = BoardKind.MeatballFlag;
                }
                else if (s.TimePenaltySeconds > 0)
                {
                    board = BoardKind.TimePenalty;
                    boardValue = s.TimePenaltySeconds;
                }
                else if (s.CountdownLaps > 0)
                {
                    board = BoardKind.Countdown;
                    boardValue = s.CountdownLaps;
                }
                else if (s.StartPhase != StartPhase.Off)
                {
                    board = BoardKind.StartGantry;
                    boardValue = s.StartLightsLit;
                }
            }

            // Frame slot: suppressed by takeovers and by DQ (your race is
            // over — heads-up advisories are moot). Furled > incident.
            FrameKind frame = FrameKind.None;
            if (!takeover && !disqualified)
            {
                if (s.Furled)
                {
                    frame = FrameKind.Furled;
                }
                else if (s.IncidentWarning)
                {
                    frame = FrameKind.Incident;
                }
            }

            // Strip: local yellows only — never under takeovers, never for
            // full-course states (the whole track is the sector).
            bool strip = !takeover && s.Caution == Caution.None && !s.Sectors.IsEmpty;

            return new Composition
            {
                Connected = true,
                Field = field,
                FieldTier = tier,
                Board = board,
                BoardValue = boardValue,
                Frame = frame,
                SectorStrip = strip,
            };
        }

        private static FieldKind TrackField(TrackFlag flag)
        {
            switch (flag)
            {
                case TrackFlag.Blue:
                    return FieldKind.Blue;
                case TrackFlag.White:
                    return FieldKind.White;
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

        /// <summary>Field kinds that yield to the caution-forced yellow field.</summary>
        private static bool Yields(FieldKind field) =>
            field == FieldKind.None
            || field == FieldKind.Green
            || field == FieldKind.Blue
            || field == FieldKind.White
            || field == FieldKind.Debris;
    }
}
