// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Host-side state model for the renderer — mirrors the vocabulary of
// proto's State (flag / wave / session / caution / sectors) with the
// PascalCase variant names used by testdata/frames/manifest.json. This
// model is host-only and never crosses the wire; the v2 wire protocol
// carries rendered frames, not state.

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
    /// Black-flag detail (M10 penalty suite, host-only): which service the
    /// black flag orders. <see cref="None"/> renders the plain golden-frozen
    /// black-flag X; the other two add a DT / SG text marker. No current
    /// adapter can populate this from telemetry (iRacing's SessionFlags has
    /// a single <c>black</c> bit — verified against the iRacing SDK shipped
    /// with SimHub 9.11.21); the dimension exists for sims that do expose
    /// the distinction (e.g. the Codemasters F1 UDP penalty events).
    /// </summary>
    public enum BlackFlagDetail
    {
        None,
        DriveThrough,
        StopAndGo,
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

        /// <summary>No sectors flagged.</summary>
        public static SectorSet Empty => new SectorSet(0);

        /// <summary>Build from a raw low-3-bit mask (mirrors <c>SectorMask::from_bits</c>).</summary>
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

        /// <summary>Whether no sector is flagged.</summary>
        public bool IsEmpty => _bits == 0;

        public bool Equals(SectorSet other) => _bits == other._bits;

        public override bool Equals(object obj) => obj is SectorSet other && Equals(other);

        public override int GetHashCode() => _bits;
    }

    /// <summary>
    /// Everything <see cref="Effects.Paint"/> needs besides the frame
    /// counter. The first five fields are the C# mirror of
    /// <c>proto::State</c>; the M10 penalty dimensions below them are
    /// host-only and never cross the wire (the v2 protocol carries rendered
    /// frames). Every penalty default means "none", so any state built
    /// without touching them renders byte-identically to the pre-M10
    /// renderer — the 40 ported-parity goldens pin this.
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
        /// above 3 render as 3. Named for the iRacing "SLOW DOWN" penalty,
        /// but graded so richer sources can scale it. iRacing telemetry
        /// exposes no graded slow-down meter (verified — see
        /// docs/simhub-flag-properties.md), so today only the preview tour
        /// and future adapters set it.
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
        /// Mirror of Rust <c>State::default()</c>: no flag, no wave, session
        /// unknown, no caution, no sectors — and no penalty state, keeping
        /// every M10 code path unreachable by default.
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
        };
    }
}
