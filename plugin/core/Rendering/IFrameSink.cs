// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The consumer side of the one-renderer→N-sinks interface. Sinks sample the
// renderer's 60 fps internal clock — they never own a clock of their own for
// animation purposes.

namespace Uniflag.Rendering
{
    /// <summary>
    /// A consumer of finished frames, invoked by <see cref="RendererLoop"/>
    /// on its render thread once per completed tick.
    /// </summary>
    public interface IFrameSink
    {
        /// <summary>
        /// Deliver one completed frame.
        ///
        /// <para><b>Buffer ownership:</b> <paramref name="rgb888"/> is the
        /// renderer's reusable publish buffer (32×32 RGB888, row-major,
        /// 3072 bytes — golden-frame layout). It is on loan only for the
        /// duration of this call: read it synchronously or copy what you
        /// need; never write to it and never retain the reference — its
        /// contents are overwritten on a later tick.</para>
        ///
        /// <para><b>Threading:</b> called on the dedicated render thread.
        /// Implementations must not block (a stalled sink delays every other
        /// sink and the frame clock's render cadence) and must not call back
        /// into <see cref="RendererLoop.AddSink"/> /
        /// <see cref="RendererLoop.RemoveSink"/>. Exceptions are isolated by
        /// the loop — a throwing sink never takes down the renderer or its
        /// sibling sinks.</para>
        /// </summary>
        /// <param name="rgb888">Borrowed 3072-byte RGB888 frame; valid only during the call.</param>
        /// <param name="frameIndex">The renderer's 60 fps tick index for this frame —
        /// strictly increasing, but with gaps when the loop skips missed ticks.</param>
        void OnFrame(byte[] rgb888, long frameIndex);
    }
}
