// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;

namespace Mykeels.CSharpRepl.Mcp;

/// <summary>
/// Writable locations for caches and for child processes (NuGet downloads, MSBuild temp). %TEMP% is not
/// always usable: a sandboxed or restricted host can run the server with a token that allows only the
/// install directory, and MSBuild then fails with MSB1025 / UnauthorizedAccessException on
/// CreateTempSubdirectory. Every candidate is probed once, in order, and the first writable one wins.
/// </summary>
internal static class McpCache
{
    private static readonly Lazy<string> RootLazy = new(ResolveRoot);

    internal static string Root => RootLazy.Value;

    internal static string TempDirectory => EnsureDirectory(Path.Combine(Root, "tmp"));

    internal static string PackageDirectory => EnsureDirectory(Path.Combine(Root, "packages"));

    private static string ResolveRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("CSHARPREPL_MCP_CACHE"),
            Combine(AppContext.BaseDirectory, "cache"),
            Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "csharprepl-mcp"
            ),
            Combine(Path.GetTempPath(), "csharprepl-mcp"),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TryCreate(candidate))
            {
                return candidate!;
            }
        }

        // Nothing is writable: report the usual location so the failure names a real path.
        return Combine(Path.GetTempPath(), "csharprepl-mcp")!;
    }

    private static string? Combine(string? directory, string child) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, child);

    private static bool TryCreate(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Points a child process at writable temp/cache directories. Without this, a build or restore launched
    /// from a restricted host fails before it reads a single project file.
    /// </summary>
    internal static void ConfigureChildEnvironment(ProcessStartInfo startInfo)
    {
        var temp = TempDirectory;
        startInfo.Environment["TMP"] = temp;
        startInfo.Environment["TEMP"] = temp;
        startInfo.Environment["TMPDIR"] = temp;

        var home = EnsureDirectory(Path.Combine(Root, "home"));
        startInfo.Environment["DOTNET_CLI_HOME"] = home;

        // Same first-run banner problem as any fresh CLI home: keep build output readable.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
    }
}
