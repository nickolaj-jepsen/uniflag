// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Host-side state model for the renderer — mirrors proto's State (flag /
// wave / session / caution / sectors) with the PascalCase variant names used
// by testdata/frames/manifest.json. Host-only, never crosses the wire; the
// v2 protocol carries rendered frames, not state.

using System;

namespace Uniflag.Rendering
{
    /// <summary>Track flag, mirroring <c>proto::Flag</c>.</summary>
    public enum Flag
    {
        None,
        Yellow,
        Blue,
        Black,
        White,
        Red,
        Green,
        Checkered,
        Orange,
    }

    /// <summary>Flag wave level, mirroring <c>proto::WaveLevel</c>.</summary>
    public enum WaveLevel
    {
        None,
        Single,
        Double,
    }

    /// <summary>Session state, mirroring <c>proto::Session</c>.</summary>
    public enum Session
    {
        PreRace,
        Racing,
        Paused,
        PostRace,
        Replay,
        Unknown,
    }

    /// <summary>Caution state, mirroring <c>proto::Caution</c>. Orthogonal to the flag.</summary>
    public enum Caution
    {
        None,
        VirtualSafetyCar,
        SafetyCar,
    }

    /// <summary>
    /// Which service the black flag orders. <see cref="None"/> renders the
    /// plain golden-frozen black-flag X; the other two add a DT / SG text
    /// marker. No current adapter populates this — iRacing's SessionFlags has
    /// a single <c>black</c> bit; the dimension exists for sims that expose
    /// the distinction (e.g. Codemasters F1 UDP penalty events).
    /// </summary>
    public enum BlackFlagDetail
    {
        None,
        DriveThrough,
        StopAndGo,
    }

    /// <summary>
    /// Standing/rolling start-light gantry phase (iRacing SessionFlags
    /// <c>startReady</c>/<c>startSet</c>/<c>startGo</c>, plus
    /// <c>oneLapToGreen</c> folded into <see cref="Ready"/> and the furled
    /// pre-start <c>greenHeld</c> into <see cref="Set"/>). Host-only, like
    /// the penalty dimensions — a start signal is a device presentation, not a
    /// wire flag. <see cref="Off"/> is the default so any state built without
    /// touching it renders exactly as the frozen corpus pins it.
    /// </summary>
    public enum StartLights
    {
        Off,
        Ready,
        Set,
        Go,
    }

    /// <summary>
    /// Set of flagged sectors 1..=3, mirroring <c>proto::SectorMask</c>
    /// (sector n is bit n-1 of the low 3 bits).
    /// </summary>
    public readonly struct SectorSet : IEquatable<SectorSet>
    {
        private readonly byte _bits;

        private SectorSet(byte bits)
        {
            _bits = bits;
        }

        public static SectorSet Empty => new SectorSet(0);

        /// <summary>Build from a raw low-3-bit mask.</summary>
        public static SectorSet FromBits(byte bits) => new SectorSet((byte)(bits & 0b111));

        /// <summary>This set plus sector <paramref name="sector"/> (1..=3).</summary>
        public SectorSet With(int sector)
        {
            if (sector < 1 || sector > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(sector), sector, "sector must be 1..=3");
            }
            return new SectorSet((byte)(_bits | (1 << (sector - 1))));
        }

        /// <summary>Whether sector <paramref name="sector"/> (1..=3) is flagged.</summary>
        public bool Contains(int sector) =>
            sector >= 1 && sector <= 3 && (_bits & (1 << (sector - 1))) != 0;

        public bool IsEmpty => _bits == 0;

        public bool Equals(SectorSet other) => _bits == other._bits;

        public override bool Equals(object obj) => obj is SectorSet other && Equals(other);

        public override int GetHashCode() => _bits;
    }

    /// <summary>
    /// Everything <see cref="Effects.Paint"/> needs besides the frame
    /// counter. The first five fields mirror <c>proto::State</c>; the penalty
    /// dimensions below them are host-only and never cross the wire. Every
    /// penalty default means "none", so any state built without touching them
    /// renders byte-identically — the 40 ported-parity goldens pin this.
    /// </summary>
    public struct RenderState
    {
        public Flag Flag;
        public WaveLevel Wave;
        public Session Session;
        public Caution Caution;
        public SectorSet Sectors;

        /// <summary>
        /// Slow-down alert severity, 0 (none) to 3 (most urgent); values
        /// above 3 render as 3. Graded so richer sources can scale it, but
        /// iRacing exposes no graded slow-down meter (see
        /// docs/simhub-flag-properties.md), so today only the preview tour
        /// sets it above 0.
        /// </summary>
        public byte Slowdown;

        /// <summary>
        /// Meatball / mechanical black flag (iRacing SessionFlags
        /// <c>repair</c> bit): mandatory pit for repairs. Renders as its own
        /// board, visually distinct from the orange quadrant effect.
        /// </summary>
        public bool Meatball;

        /// <summary>Black-flag service detail; only consumed when <see cref="Flag"/> is <see cref="Flag.Black"/>.</summary>
        public BlackFlagDetail BlackDetail;

        /// <summary>
        /// Furled black/white warning (iRacing SessionFlags <c>furled</c>
        /// bit): a warning accent overlaid on the base layer, not a full
        /// board.
        /// </summary>
        public bool Furled;

        /// <summary>
        /// Start-light gantry phase. Rendered as its own board only when no
        /// flag, caution or penalty claims the base (the <see cref="Flag.None"/>
        /// idle arm) — a real flag always supersedes it, and at "GO" the green
        /// flag naturally takes over. <see cref="StartLights.Off"/> means "no
        /// start sequence", keeping the board code path unreachable by default.
        /// </summary>
        public StartLights StartLights;

        /// <summary>
        /// Debris / surface warning (iRacing SessionFlags <c>debris</c> bit):
        /// a yellow-and-red striped hazard board. Like the start-light board it
        /// only paints in the <see cref="Flag.None"/> idle arm — a unified flag
        /// (which already conveys caution) supersedes it.
        /// </summary>
        public bool Debris;

        /// <summary>
        /// Incident-limit warning (host-derived: iRacing
        /// <c>PlayerCarMyIncidentCount</c> within a margin of the session
        /// incident limit). A blinking-red-frame accent overlaid on whatever
        /// base is showing — suppressed under red / disconnected like the
        /// furled accent, since it is a heads-up while racing continues.
        /// </summary>
        public bool IncidentWarning;

        /// <summary>
        /// All-none default: session unknown, no penalty state, keeping every
        /// penalty code path unreachable by default.
        /// </summary>
        public static RenderState Default => new RenderState
        {
            Flag = Flag.None,
            Wave = WaveLevel.None,
            Session = Session.Unknown,
            Caution = Caution.None,
            Sectors = SectorSet.Empty,
            Slowdown = 0,
            Meatball = false,
            BlackDetail = BlackFlagDetail.None,
            Furled = false,
            StartLights = StartLights.Off,
            Debris = false,
            IncidentWarning = false,
        };
    }
}
