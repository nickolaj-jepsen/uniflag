// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The whole signal vocabulary on one page.
//
// Two artefacts, because two audiences read them differently:
//
//  - contact-sheet.png — every scenario in catalogue order on a single
//    grid. No labels: naming the cells would mean hand-authoring a pixel
//    font, and the printed running order does the same job. This is the
//    one file to open (or attach to a review) to answer "did anything move
//    that I didn't mean to move".
//  - contact-sheet.html — the same grid with real text labels and
//    descriptions, each cell an embedded data-URI PNG. Self-contained, so
//    it opens from disk with no server.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Uniflag.Tools
{
    internal static class ContactSheet
    {
        private const int Gutter = 6;

        /// <summary>Background behind and between the cells — mid-grey, so a
        /// black frame is still visible as a cell rather than a hole.</summary>
        private static readonly (byte R, byte G, byte B) Backdrop = (48, 48, 56);

        internal sealed class Cell
        {
            public Cell(string name, string description, byte[] rgb)
            {
                Name = name;
                Description = description;
                Rgb = rgb;
            }

            public string Name { get; }
            public string Description { get; }
            public byte[] Rgb { get; }
        }

        /// <summary>Compose the cells into one RGB image, row-major.</summary>
        public static (byte[] Rgb, int Width, int Height) Compose(
            IReadOnlyList<Cell> cells, int frameSize, int scale, int columns)
        {
            int cell = frameSize * scale;
            int rows = (cells.Count + columns - 1) / columns;
            int width = columns * cell + (columns + 1) * Gutter;
            int height = rows * cell + (rows + 1) * Gutter;

            var canvas = new byte[width * height * 3];
            for (int i = 0; i < canvas.Length; i += 3)
            {
                canvas[i] = Backdrop.R;
                canvas[i + 1] = Backdrop.G;
                canvas[i + 2] = Backdrop.B;
            }

            for (int i = 0; i < cells.Count; i++)
            {
                int col = i % columns;
                int row = i / columns;
                int originX = Gutter + col * (cell + Gutter);
                int originY = Gutter + row * (cell + Gutter);
                Blit(canvas, width, cells[i].Rgb, frameSize, scale, originX, originY);
            }

            return (canvas, width, height);
        }

        private static void Blit(
            byte[] canvas, int canvasWidth, byte[] frame, int frameSize, int scale, int originX, int originY)
        {
            for (int y = 0; y < frameSize * scale; y++)
            {
                int srcRow = (y / scale) * frameSize * 3;
                for (int x = 0; x < frameSize * scale; x++)
                {
                    int src = srcRow + (x / scale) * 3;
                    int dst = ((originY + y) * canvasWidth + originX + x) * 3;
                    canvas[dst] = frame[src];
                    canvas[dst + 1] = frame[src + 1];
                    canvas[dst + 2] = frame[src + 2];
                }
            }
        }

        /// <summary>
        /// A self-contained labelled page. Each cell carries its own PNG as a
        /// data URI, so the file works from `file://` with nothing alongside it.
        /// </summary>
        public static string BuildHtml(IReadOnlyList<Cell> cells, int frameSize, int scale)
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>uniflag — grammar contact sheet</title>
<style>
  :root { color-scheme: light dark; }
  body {
    margin: 0; padding: 2rem;
    background: #16161a; color: #e8e8ea;
    font: 14px/1.5 ui-sans-serif, system-ui, -apple-system, ""Segoe UI"", sans-serif;
  }
  h1 { font-size: 1.1rem; font-weight: 600; margin: 0 0 .25rem; }
  .sub { color: #9a9aa4; margin: 0 0 2rem; }
  .grid {
    display: grid; gap: 1.25rem;
    grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  }
  figure { margin: 0; }
  img {
    display: block; width: 100%; height: auto;
    image-rendering: pixelated;
    border-radius: 6px; background: #000;
  }
  figcaption { margin-top: .5rem; }
  .name { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; }
  .desc { color: #9a9aa4; font-size: 12px; }
</style>
</head>
<body>
<h1>uniflag — grammar contact sheet</h1>
<p class=""sub"">");
            sb.Append(cells.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(@" scenarios from the catalogue, each replayed from frame 0 to its sample frame.</p>
<div class=""grid"">
");
            foreach (Cell c in cells)
            {
                byte[] png = Png.Encode(c.Rgb, frameSize, frameSize, scale);
                sb.Append("<figure><img alt=\"").Append(Escape(c.Name)).Append("\" src=\"data:image/png;base64,")
                  .Append(Convert.ToBase64String(png)).Append("\">")
                  .Append("<figcaption><div class=\"name\">").Append(Escape(c.Name)).Append("</div>")
                  .Append("<div class=\"desc\">").Append(Escape(c.Description)).Append("</div>")
                  .Append("</figcaption></figure>\n");
            }
            sb.Append("</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
