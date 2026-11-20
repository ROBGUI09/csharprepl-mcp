// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{

    private static async Task<(List<string> Added, List<string> Failed)> AddAssemblyFilesAsync(
        IReadOnlyList<string> files, CSharpSession session, CancellationToken cancellationToken)
    {
        var added = new List<string>();
        var failed = new List<string>();
        var unique = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length == 0) return (added, failed);

        var directives = new StringBuilder();
        foreach (var file in unique)
        {
            directives.Append("#r \"").Append(file.Replace('\\', '/')).Append("\"\n");
        }

        var batch = await session
            .EvaluateAsync(directives.ToString(), null, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        if (!batch.IsError)
        {
            foreach (var file in unique)
            {
                added.Add(file);
                session.TrackReference(file);
            }
            return (added, failed);
        }

        foreach (var file in unique)
        {
            var single = await session
                .EvaluateAsync("#r \"" + file.Replace('\\', '/') + "\"", null, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (single.IsError)
            {
                failed.Add(Path.GetFileName(file) + " :: " + OneLine(single.Error));
            }
            else
            {
                added.Add(file);
                session.TrackReference(file);
            }
        }
        return (added, failed);
    }

    private static async Task<ToolOutcome> AddPackageAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var id = RequireString(args, "id").Trim();
        var version = OptionalString(args, "version")?.Trim();
        var idLower = id.ToLowerInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            if (version is null)
            {
                var indexJson = await http
                    .GetStringAsync("https://api.nuget.org/v3-flatcontainer/" + idLower + "/index.json", cancellationToken)
                    .ConfigureAwait(false);
                using var index = JsonDocument.Parse(indexJson);
                var versions = index.RootElement.GetProperty("versions").EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => v is not null)
                    .Select(v => v!)
                    .ToArray();
                if (versions.Length == 0) return Fail("Package '" + id + "' has no published versions.");
                version = versions.LastOrDefault(v => !v.Contains('-')) ?? versions[versions.Length - 1];
            }

            var url = "https://api.nuget.org/v3-flatcontainer/" + idLower + "/" + version + "/" + idLower + "." + version + ".nupkg";
            var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

            var root = Path.Combine(McpCache.PackageDirectory, idLower, version);
            Directory.CreateDirectory(root);
            var nupkg = Path.Combine(root, idLower + "." + version + ".nupkg");
            await File.WriteAllBytesAsync(nupkg, bytes, cancellationToken).ConfigureAwait(false);

            var extract = Path.Combine(root, "extracted");
            if (!Directory.Exists(extract))
            {
                ZipFile.ExtractToDirectory(nupkg, extract);
            }

            var libRoot = Path.Combine(extract, "lib");
            if (!Directory.Exists(libRoot))
            {
                var top = Directory.GetDirectories(extract).Select(Path.GetFileName);
                return Fail("Package " + id + " " + version + " has no lib/ folder (top-level: " +
                            string.Join(", ", top) + "). It may be a tooling-only package.");
            }

            string[] preferred = { "net8.0", "net9.0", "net10.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "net5.0", "netcoreapp3.1", "net462", "net472", "net48" };
            var folders = Directory.GetDirectories(libRoot).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToArray();
            var chosen = preferred.FirstOrDefault(p => folders.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase)))
                         ?? folders.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            if (chosen is null) return Fail("Package " + id + " has no usable lib/<tfm> folder.");

            var folder = Path.Combine(libRoot, folders.First(f => string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase)));
            var dlls = Directory.GetFiles(folder, "*.dll");

            var (added, failed) = await AddAssemblyFilesAsync(dlls, session, cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("package: ").Append(id).Append(' ').Append(version).Append('\n');
            sb.Append("target: lib/").Append(chosen).Append(" (").Append(added.Count).Append(" dll referenced)\n");
            foreach (var file in added) sb.Append("  + ").Append(Path.GetFileName(file)).Append('\n');
            if (failed.Count > 0)
            {
                sb.Append("failed: ").Append(string.Join("; ", failed)).Append('\n');
            }
            sb.Append("Transitive dependencies are not resolved automatically - add_package them if types fail to load.");
            return Truncate(sb.ToString(), context.MaxChars);
        }
        catch (HttpRequestException ex)
        {
            return Fail("Could not reach nuget.org: " + OneLine(ex.Message));
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + OneLine(ex.Message));
        }
    }
}
