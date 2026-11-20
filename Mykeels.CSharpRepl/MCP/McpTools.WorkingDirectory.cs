// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Mykeels.CSharpRepl.Mcp;

/// <summary>
/// Working-directory plumbing. The server is deliberately not bound to one project: every tool that
/// touches the file system accepts an optional "cwd", and the process directory is only a fallback.
/// </summary>
public static partial class McpTools
{
    internal static string ResolveWorkingDirectory(JsonObject args, ToolContext context)
    {
        var raw = OptionalString(args, "cwd");
        var candidate = Environment.ExpandEnvironmentVariables(raw ?? context.WorkingDirectory);
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid cwd '" + candidate + "': " + ex.Message);
        }

        if (!Directory.Exists(full))
        {
            throw new ArgumentException("Working directory not found: " + full);
        }

        return full;
    }

    internal static string ResolvePath(string path, string workingDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        var combined = Path.IsPathRooted(expanded) ? expanded : Path.Combine(workingDirectory, expanded);
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Temporarily points the process at a directory: Roslyn resolves relative #r and file IO against
    /// Environment.CurrentDirectory, and tool calls are dispatched sequentially, so a scoped switch is safe.
    /// </summary>
    internal static ScopedDirectory EnterDirectory(string directory) => new(directory);

    internal readonly struct ScopedDirectory : IDisposable
    {
        private readonly string? _previous;

        public ScopedDirectory(string directory)
        {
            try
            {
                _previous = Environment.CurrentDirectory;
                if (!string.Equals(_previous, directory, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.CurrentDirectory = directory;
                }
            }
            catch
            {
                _previous = null;
            }
        }

        public void Dispose()
        {
            if (_previous is null)
            {
                return;
            }

            try
            {
                if (!string.Equals(Environment.CurrentDirectory, _previous, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.CurrentDirectory = _previous;
                }
            }
            catch
            {
                // the previous directory disappeared while the call ran; keep the current one
            }
        }
    }
}
