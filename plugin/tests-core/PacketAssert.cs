// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using Uniflag.Protocol;
using Xunit;

namespace Uniflag.Tests
{
    /// <summary>
    /// Packet equivalence for tests: same concrete type, identical raw
    /// encoding. <see cref="Packet"/> deliberately has no value equality —
    /// production never compares packets, and the byte form is the contract.
    /// </summary>
    internal static class PacketAssert
    {
        internal static void Same(Packet expected, Packet actual)
        {
            Assert.NotNull(actual);
            Assert.IsType(expected.GetType(), actual);
            Assert.Equal(expected.EncodeRaw(), actual.EncodeRaw());
        }
    }
}
