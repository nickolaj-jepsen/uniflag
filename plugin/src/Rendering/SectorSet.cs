// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Sector-yellow mask, shared by the adapter pipeline and the Grammar
// renderer (docs/flag-grammar.md §6.5). Host-only — the v2 wire protocol
// carries rendered frames, not state.

using System;

namespace Uniflag.Rendering
{
    /// <summary>
    /// Set of flagged sectors 1..=3 (sector n is bit n-1 of the low 3 bits).
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
}
