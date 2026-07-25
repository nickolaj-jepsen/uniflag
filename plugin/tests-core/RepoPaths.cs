// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.IO;
using System.Reflection;

namespace Uniflag.Tests
{
    /// <summary>
    /// Locates repo-relative fixture directories from wherever the test
    /// runner drops the assembly (typically
    /// <c>plugin/tests/bin/&lt;Config&gt;/net48/</c>) by probing upward for a
    /// directory that contains <c>testdata</c>.
    /// </summary>
    internal static class RepoPaths
    {
        /// <summary>Absolute path of <c>testdata/proto/</c> — the frozen golden vectors.</summary>
        public static string TestDataProtoDir => Path.Combine(RepoRoot, "testdata", "proto");

        /// <summary>Absolute path of the repo root (the ancestor holding <c>testdata/</c>).</summary>
        public static string RepoRoot { get; } = LocateRepoRoot();

        /// <summary>Read one golden vector file from <c>testdata/proto/</c>.</summary>
        public static byte[] ReadVector(string name)
        {
            string path = Path.Combine(TestDataProtoDir, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"missing golden vector {path}; regenerate via just golden-regen", path);
            }
            return File.ReadAllBytes(path);
        }

        private static string LocateRepoRoot()
        {
            // AppContext.BaseDirectory is where the runner executes; the
            // assembly location is the fallback for shadow-copying runners.
            string[] anchors =
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            };
            foreach (string anchor in anchors)
            {
                if (string.IsNullOrEmpty(anchor))
                {
                    continue;
                }
                for (var dir = new DirectoryInfo(anchor); dir != null; dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "testdata")))
                    {
                        return dir.FullName;
                    }
                }
            }
            throw new InvalidOperationException(
                "could not locate the repo root: no ancestor of " +
                $"'{AppContext.BaseDirectory}' contains a 'testdata' directory");
        }
    }
}
