// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The whole signal vocabulary on one page: contact-sheet.html, every
// scenario in catalogue order with its name and description, each cell an
// embedded data-URI PNG. Self-contained, so it opens from disk with no
// server, and each cell can be saved or copied on its own. For one frame as
// a standalone image, `just frames <scenario>`.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Uniflag.Tools
{
    internal static class ContactSheet
    {
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
