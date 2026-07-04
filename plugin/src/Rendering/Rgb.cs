// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// 8-bit-per-channel RGB triple (docs/effects-spec.md §1).

using System;

namespace Uniflag.Rendering
{
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;

        public override bool Equals(object obj) => obj is Rgb other && Equals(other);

        public override int GetHashCode() => (R << 16) | (G << 8) | B;

        public override string ToString() => $"({R},{G},{B})";
    }
}
