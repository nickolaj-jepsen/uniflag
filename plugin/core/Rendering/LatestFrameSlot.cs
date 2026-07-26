// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;

namespace Uniflag.Rendering
{
    /// <summary>
    /// A depth-one, newest-wins frame slot: the hand-off between the render
    /// thread and one slower consumer (USB TX pump, overlay socket, WPF
    /// dispatcher). <see cref="Post"/> never blocks beyond one bounded buffer
    /// copy behind a short lock; a frame posted while one is pending
    /// overwrites it — stale frames are dropped, never queued. Single
    /// consumer; the caller owns the wake-up mechanism.
    /// </summary>
    public sealed class LatestFrameSlot
    {
        private readonly object _gate = new object();
        private readonly byte[] _latest;
        private bool _pending;

        public LatestFrameSlot(int frameLength)
        {
            _latest = new byte[frameLength];
        }

        /// <summary>
        /// Copy the newest frame in. True when the slot was empty — the one
        /// post per drain that owes the consumer a wake-up; coalesced posts
        /// return false and ride the earlier wake.
        /// </summary>
        public bool Post(byte[] frame)
        {
            lock (_gate)
            {
                Buffer.BlockCopy(frame, 0, _latest, 0, _latest.Length);
                if (_pending)
                {
                    return false;
                }
                _pending = true;
                return true;
            }
        }

        /// <summary>
        /// Copy the pending frame out and empty the slot. False when nothing
        /// is pending.
        /// </summary>
        public bool TryTake(byte[] destination)
        {
            lock (_gate)
            {
                if (!_pending)
                {
                    return false;
                }
                Buffer.BlockCopy(_latest, 0, destination, 0, _latest.Length);
                _pending = false;
                return true;
            }
        }

        /// <summary>Drop any pending frame (session reset).</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _pending = false;
            }
        }
    }
}
