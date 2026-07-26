// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The settings-tab preview sink: the WPF side of the sink boundary. The
// rendering core in plugin/core/Rendering/ stays WPF-free; only this layer
// touches System.Windows.*.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Uniflag.Rendering;

namespace Uniflag
{
    /// <summary>
    /// <see cref="IFrameSink"/> that mirrors the renderer's output into a
    /// 32×32 <see cref="WriteableBitmap"/> (Rgb24 — same byte layout as the
    /// renderer's RGB888 frames, so the update is a straight copy). Bind
    /// <see cref="Bitmap"/> as an <c>Image.Source</c>.
    ///
    /// <para><b>Threading:</b> <see cref="OnFrame"/> runs on the render
    /// thread and never blocks on the UI — it snapshots under a lock and
    /// queues at most one Dispatcher operation. Updates coalesce: while the
    /// UI hasn't caught up, newer frames overwrite the snapshot and ride the
    /// already-queued operation, so only the newest frame is ever painted and
    /// no backlog can build.</para>
    /// </summary>
    public sealed class WpfPreviewSink : IFrameSink
    {
        private static readonly Int32Rect FullRect =
            new Int32Rect(0, 0, FrameBuffer.Width, FrameBuffer.Height);

        private const int Stride = FrameBuffer.Width * 3;

        private readonly Dispatcher _dispatcher;
        private readonly WriteableBitmap _bitmap;
        private readonly Action _publish; // cached delegate — no per-frame allocation
        private readonly LatestFrameSlot _slot = new LatestFrameSlot(FrameBuffer.ByteLength);
        private readonly byte[] _staging = new byte[FrameBuffer.ByteLength];

        /// <summary>
        /// Construct on the UI thread that owns <paramref name="dispatcher"/>
        /// (WriteableBitmap has thread affinity).
        /// </summary>
        public WpfPreviewSink(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _bitmap = new WriteableBitmap(
                FrameBuffer.Width, FrameBuffer.Height, 96, 96, PixelFormats.Rgb24, null);
            _publish = Publish;
        }

        /// <summary>The live preview bitmap. Touch from the UI thread only.</summary>
        public BitmapSource Bitmap => _bitmap;

        /// <inheritdoc />
        public void OnFrame(byte[] rgb888, long frameIndex)
        {
            if (_slot.Post(rgb888))
            {
                _dispatcher.BeginInvoke(_publish, DispatcherPriority.Render);
            }
        }

        private void Publish()
        {
            if (_slot.TryTake(_staging))
            {
                _bitmap.WritePixels(FullRect, _staging, Stride, 0);
            }
        }
    }
}
