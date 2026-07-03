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
    /// counter — the C# mirror of <c>proto::State</c>.
    /// </summary>
    public struct RenderState
    {
        public Flag Flag;
        public WaveLevel Wave;
        public Session Session;
        public Caution Caution;
        public SectorSet Sectors;

        /// <summary>
        /// Mirror of Rust <c>State::default()</c>: no flag, no wave, session
        /// unknown, no caution, no sectors.
        /// </summary>
        public static RenderState Default => new RenderState
        {
            Flag = Flag.None,
            Wave = WaveLevel.None,
            Session = Session.Unknown,
            Caution = Caution.None,
            Sectors = SectorSet.Empty,
        };
    }
}
