// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{
    // ---------------- assemblies ----------------

    private static async Task<ToolOutcome> AddReferenceAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var requested = OptionalStringArray(args, "paths");
        if (requested.Count == 0) return Fail("'paths' is required and must contain at least one DLL or folder.");

        var workingDirectory = ResolveWorkingDirectory(args, context);
        var recursive = OptionalBool(args, "recursive");
        var files = new List<string>();
        var missing = new List<string>();

        foreach (var raw in requested)
        {
            var full = ResolvePath(raw, workingDirectory);
            if (Directory.Exists(full))
            {
                files.AddRange(Directory.GetFiles(
                    full, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            }
            else if (File.Exists(full))
            {
                files.Add(full);
            }
            else
            {
                missing.Add(full);
            }
        }

        if (files.Count == 0)
        {
            return Fail("Nothing to load. Missing: " + string.Join(", ", missing));
        }

        var loadFailures = new List<string>();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Assembly.LoadFrom(file);
            }
            catch (Exception ex)
            {
                loadFailures.Add(Path.GetFileName(file) + ": " + OneLine(ex.Message));
            }
        }

        var (added, failed) = await AddAssemblyFilesAsync(files, session, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("referenced ").Append(added.Count).Append(" of ")
          .Append(files.Distinct(StringComparer.OrdinalIgnoreCase).Count())
          .Append(" assembly file(s).\n");
        foreach (var file in added) sb.Append("  + ").Append(file).Append('\n');
        if (failed.Count > 0)
        {
            sb.Append("failed to reference (still loaded into the process, so reflection may see them):\n");
            foreach (var f in failed) sb.Append("  - ").Append(f).Append('\n');
        }
        if (loadFailures.Count > 0)
        {
            sb.Append("load warnings:\n");
            foreach (var f in loadFailures) sb.Append("  ! ").Append(f).Append('\n');
        }
        if (missing.Count > 0)
        {
            sb.Append("missing paths: ").Append(string.Join(", ", missing)).Append('\n');
        }
        sb.Append("Loaded assemblies are available to later run_script calls and to list_types/describe_type.");
        return Truncate(sb.ToString(), context.MaxChars);
    }
}
