// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The settings-tab preview sink: the WPF side of the sink boundary. The
// rendering core in plugin/src/Rendering/ stays WPF-free; only this layer
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
        private readonly object _gate = new object();
        private readonly byte[] _latest = new byte[FrameBuffer.ByteLength];
        private readonly byte[] _staging = new byte[FrameBuffer.ByteLength];
        private bool _updateQueued;

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
            lock (_gate)
            {
                Buffer.BlockCopy(rgb888, 0, _latest, 0, FrameBuffer.ByteLength);
                if (_updateQueued)
                {
                    return; // coalesce: the queued Publish will pick this frame up
                }
                _updateQueued = true;
            }
            _dispatcher.BeginInvoke(_publish, DispatcherPriority.Render);
        }

        private void Publish()
        {
            lock (_gate)
            {
                Buffer.BlockCopy(_latest, 0, _staging, 0, FrameBuffer.ByteLength);
                _updateQueued = false;
            }
            _bitmap.WritePixels(FullRect, _staging, Stride, 0);
        }
    }
}
