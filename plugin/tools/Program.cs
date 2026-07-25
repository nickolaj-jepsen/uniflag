// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// uniflag-frames — look at what the renderer actually paints.
//
// The panel is a picture, and every other way to see one — SimHub, hardware,
// the web overlay — needs a Windows SimHub install, which makes renderer
// iteration a guessing game and a visual regression invisible until someone
// plugs a panel in. This renders any catalogue scenario straight to PNG (or
// the terminal) on any OS, with no SimHub, no hardware, and no game.
//
// Usage:
//   list                                 catalogue names + descriptions
//   render <scenario> [--frame N] [--scale N] [--out PATH] [--rgb]
//   sheet  [--scale N] [--columns N] [--out DIR]
//   ansi   <scenario|PATH.rgb> [--frame N]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Uniflag.Rendering;

namespace Uniflag.Tools
{
    public static class Program
    {
        private const int FrameSize = FrameBuffer.Width;
        private const int DefaultScale = 8;
        private const int DefaultSheetScale = 4;
        private const int DefaultColumns = 7;
        private const string DefaultOutDir = "target/frames";

        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "list":
                        return List();
                    case "render":
                        return Render(args);
                    case "sheet":
                        return Sheet(args);
                    case "ansi":
                        return AnsiCommand(args);
                    case "-h":
                    case "--help":
                    case "help":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine($"unknown command '{args[0]}'");
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine(@"uniflag-frames — render grammar scenarios to PNG or the terminal

  list                                       scenario names and descriptions
  render <scenario> [options]                one frame to PNG
  sheet [options]                            every scenario, one grid + a labelled HTML page
  ansi <scenario|PATH.rgb> [--frame N]       one frame as terminal half-blocks

Options:
  --frame N     frame to render (default: the scenario's sample frame)
  --scale N     nearest-neighbour upscale (default: 8 for render, 4 for sheet)
  --columns N   sheet columns (default: 7)
  --out PATH    output file (render) or directory (sheet); default target/frames
  --rgb         also write the raw 3072-byte RGB888 frame next to the PNG");
        }

        private static int List()
        {
            foreach (var sc in ScenarioCatalogue.Table)
            {
                Console.WriteLine($"{sc.Name,-38} f{sc.SampleFrame,-5} {sc.Description}");
            }
            Console.Error.WriteLine($"\n{ScenarioCatalogue.Table.Count} scenarios");
            return 0;
        }

        private static int Render(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("render: expected a scenario name (see `list`)");
                return 2;
            }

            var sc = RequireScenario(args[1]);
            uint frame = OptUInt(args, "--frame") ?? sc.SampleFrame;
            int scale = OptInt(args, "--scale") ?? DefaultScale;
            string outPath = Opt(args, "--out")
                ?? Path.Combine(DefaultOutDir, $"{sc.Name}@{frame}.png");

            byte[] rgb = ScenarioCatalogue.Render(sc, frame);
            EnsureDirectoryFor(outPath);
            File.WriteAllBytes(outPath, Png.Encode(rgb, FrameSize, FrameSize, scale));
            Console.WriteLine(Path.GetFullPath(outPath));

            if (HasFlag(args, "--rgb"))
            {
                string rgbPath = Path.ChangeExtension(outPath, ".rgb");
                File.WriteAllBytes(rgbPath, rgb);
                Console.WriteLine(Path.GetFullPath(rgbPath));
            }
            return 0;
        }

        private static int Sheet(string[] args)
        {
            int scale = OptInt(args, "--scale") ?? DefaultSheetScale;
            int columns = OptInt(args, "--columns") ?? DefaultColumns;
            string outDir = Opt(args, "--out") ?? DefaultOutDir;
            Directory.CreateDirectory(outDir);

            var cells = new List<ContactSheet.Cell>();
            foreach (var sc in ScenarioCatalogue.Table)
            {
                cells.Add(new ContactSheet.Cell(sc.Name, sc.Description, ScenarioCatalogue.Render(sc)));
            }

            var (rgb, width, height) = ContactSheet.Compose(cells, FrameSize, scale, columns);
            string pngPath = Path.Combine(outDir, "contact-sheet.png");
            File.WriteAllBytes(pngPath, Png.Encode(rgb, width, height, 1));

            string htmlPath = Path.Combine(outDir, "contact-sheet.html");
            File.WriteAllText(htmlPath, ContactSheet.BuildHtml(cells, FrameSize, scale));

            // The grid is unlabelled, so the running order IS the legend.
            Console.Error.WriteLine($"{cells.Count} scenarios, {columns} per row, reading order:");
            for (int i = 0; i < cells.Count; i++)
            {
                Console.Error.WriteLine($"  {i + 1,3}. {cells[i].Name}");
            }
            Console.WriteLine(Path.GetFullPath(pngPath));
            Console.WriteLine(Path.GetFullPath(htmlPath));
            return 0;
        }

        private static int AnsiCommand(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("ansi: expected a scenario name or a path to a .rgb frame");
                return 2;
            }

            byte[] rgb;
            string target = args[1];
            if (File.Exists(target))
            {
                rgb = File.ReadAllBytes(target);
                if (rgb.Length != FrameBuffer.ByteLength)
                {
                    throw new InvalidDataException(
                        $"{target}: expected {FrameBuffer.ByteLength} bytes, got {rgb.Length}");
                }
            }
            else
            {
                var sc = RequireScenario(target);
                rgb = ScenarioCatalogue.Render(sc, OptUInt(args, "--frame") ?? sc.SampleFrame);
            }

            Console.Out.Write(Ansi.Render(rgb, FrameSize, FrameSize));
            return 0;
        }

        private static ScenarioCatalogue.Scenario RequireScenario(string name)
        {
            var sc = ScenarioCatalogue.Find(name);
            if (sc == null)
            {
                throw new ArgumentException($"no scenario named '{name}' — run `list` to see them all");
            }
            return sc;
        }

        private static void EnsureDirectoryFor(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

        private static string Opt(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0)
            {
                return null;
            }
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{name} expects a value");
            }
            return args[i + 1];
        }

        private static int? OptInt(string[] args, string name)
        {
            string raw = Opt(args, name);
            if (raw == null)
            {
                return null;
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 1)
            {
                throw new ArgumentException($"{name} expects a positive integer, got '{raw}'");
            }
            return v;
        }

        private static uint? OptUInt(string[] args, string name)
        {
            string raw = Opt(args, name);
            if (raw == null)
            {
                return null;
            }
            if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v))
            {
                throw new ArgumentException($"{name} expects a non-negative integer, got '{raw}'");
            }
            return v;
        }
    }
}
