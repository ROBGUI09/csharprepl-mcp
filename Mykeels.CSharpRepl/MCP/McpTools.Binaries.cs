// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

/// <summary>What one managed assembly contributes: strings, dependencies, namespace sizes, entry points.</summary>
internal sealed class FileAnalysis
{
    public string Path { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsLibrary { get; init; }
    public bool Signed { get; init; }
    public IReadOnlyList<string> Strings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, int>> Namespaces { get; init; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<string> EntryPoints { get; init; } = Array.Empty<string>();
    public int TypeCount { get; init; }
}

/// <summary>One module a process has mapped, classified without trusting its file extension.</summary>
internal sealed record ModuleInfo(string Path, string Kind, string? Name, string? Version);

/// <summary>The public surface of one assembly version, one entry per type and one per public member.</summary>
internal sealed class ApiSurface
{
    public string Identity { get; init; } = string.Empty;
    public int TypeCount { get; init; }
    public SortedDictionary<string, SortedSet<string>> Members { get; } =
        new(StringComparer.Ordinal);
}

public static partial class McpTools
{
    private static readonly Regex UrlPattern = new(
        @"https?://[!-~]{4,200}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FileNamePattern = new(
        @"^[\w\-. ]+\.(dll|exe|config|xml|json|cs|csx|csproj|resx|ini|log|yml|yaml|txt|sql|db|bak|key|lic|license|pdb)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(::[A-Za-z_][A-Za-z0-9_.]*)?$", RegexOptions.Compiled);

    private static readonly Regex WindowsPathPattern = new(
        @"^[A-Za-z]:\\[!-~ ]{2,200}$", RegexOptions.Compiled);

    // ---------------- analyze_file ----------------

    private static Task<ToolOutcome> AnalyzeFileAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(args, context);
        var full = ResolvePath(RequireString(args, "path"), workingDirectory);
        if (!File.Exists(full)) return Task.FromResult(Fail("File not found: " + full));

        var includeStrings = OptionalBool(args, "strings", true);
        var filter = OptionalString(args, "string_filter");
        var stringLimit = Math.Clamp(OptionalInt(args, "strings_limit", 80), 1, 2000);

        try
        {
            var analysis = AnalyzeFile(full, includeStrings, filter);
            return Task.FromResult(Truncate(RenderFileAnalysis(analysis, stringLimit), context.MaxChars));
        }
        catch (BadImageFormatException)
        {
            return Task.FromResult(Fail("Not a valid .NET image (native or corrupted PE): " + full));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.GetType().Name + ": " + OneLine(ex.Message)));
        }
    }

    private static FileAnalysis AnalyzeFile(string full, bool includeStrings, string? filter)
    {
        using var stream = File.OpenRead(full);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException("no CLI metadata");
        }

        var md = pe.GetMetadataReader();
        var stringFilter = filter is null ? null : new Regex(filter, RegexOptions.IgnoreCase);

        // User string heap order tracks the IL, so the first endpoints, config keys and file names
        // are the ones most likely to matter.
        var strings = new List<string>();
        if (includeStrings)
        {
            foreach (var value in ReadUserStrings(full))
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 500) continue;
                if (!IsInterestingString(value, stringFilter)) continue;
                strings.Add(value);
                if (strings.Count >= 4000) break;
            }
        }

        var assemblyDef = md.IsAssembly ? md.GetAssemblyDefinition() : default;
        var dependencies = md.AssemblyReferences
            .Select(h => md.GetAssemblyReference(h))
            .Select(r => md.GetString(r.Name))
            .Where(name => !IsFrameworkAssembly(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perNamespace = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeCount = 0;
        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            var name = md.GetString(td.Name);
            if (name == "<Module>") continue;
            typeCount++;
            var ns = md.GetString(td.Namespace);
            var key = string.IsNullOrEmpty(ns) ? "(global)" : ns;
            perNamespace[key] = perNamespace.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var entryPoints = new List<string>();
        var corHeader = pe.PEHeaders.CorHeader;
        var entryToken = corHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
        if (entryToken != 0)
        {
            entryPoints.Add(
                DescribeEntryPoint(md, entryToken)
                ?? "<entry point token 0x" + entryToken.ToString("X8") + ">");
        }

        return new FileAnalysis
        {
            Path = full,
            AssemblyName = md.IsAssembly
                ? md.GetString(assemblyDef.Name)
                : md.GetString(md.GetModuleDefinition().Name),
            Version = md.IsAssembly ? assemblyDef.Version.ToString() : string.Empty,
            SizeBytes = new FileInfo(full).Length,
            IsLibrary = pe.PEHeaders.IsDll,
            Signed = corHeader is not null && (corHeader.Flags & CorFlags.StrongNameSigned) != 0,
            Strings = strings.ToArray(),
            Dependencies = dependencies,
            Namespaces = perNamespace
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            EntryPoints = entryPoints,
            TypeCount = typeCount,
        };
    }

    /// <summary>
    /// Reads the #US heap by hand: the metadata API does not enumerate user strings, and this is the
    /// heap holding the literals a compiler emitted - endpoints, config keys, SQL, file names.
    /// </summary>
    private static IReadOnlyList<string> ReadUserStrings(string assemblyPath)
    {
        var result = new List<string>();
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var rootOffset = pe.PEHeaders.MetadataStartOffset;
        var rootSize = pe.PEHeaders.MetadataSize;
        if (rootOffset <= 0 || rootSize <= 0) return result;

        var root = new byte[rootSize];
        stream.Position = rootOffset;
        var read = 0;
        while (read < rootSize)
        {
            var chunk = stream.Read(root, read, rootSize - read);
            if (chunk <= 0) break;
            read += chunk;
        }
        if (read < 24) return result;

        var position = 0;
        if (BitConverter.ToUInt32(root, position) != 0x424A5342) return result; // "BSJB"
        position += 4 + 2 + 2 + 4;
        var versionLength = (int)BitConverter.ToUInt32(root, position);
        position += 4 + versionLength;
        position += 2; // flags
        var streamCount = BitConverter.ToUInt16(root, position);
        position += 2;

        var heapOffset = -1;
        var heapSize = 0;
        for (var i = 0; i < streamCount && position + 8 <= read; i++)
        {
            var offset = (int)BitConverter.ToUInt32(root, position);
            var size = (int)BitConverter.ToUInt32(root, position + 4);
            position += 8;
            var nameStart = position;
            while (position < read && root[position] != 0) position++;
            var name = Encoding.ASCII.GetString(root, nameStart, position - nameStart);
            position++;
            position = (position + 3) & ~3;
            if (name == "#US")
            {
                heapOffset = offset;
                heapSize = size;
            }
        }

        if (heapOffset < 0 || heapSize <= 0 || heapOffset + heapSize > read) return result;

        var heap = new byte[heapSize];
        Array.Copy(root, heapOffset, heap, 0, heapSize);
        var cursor = 0;
        while (cursor < heap.Length)
        {
            if (!TryReadCompressed(heap, ref cursor, out var length) || length <= 0) break;
            var start = cursor;
            var end = Math.Min(start + length, heap.Length);
            var byteCount = end - start - 1;
            if (byteCount > 0)
            {
                var text = Encoding.Unicode.GetString(heap, start, byteCount);
                if (!string.IsNullOrEmpty(text)) result.Add(text);
            }
            cursor = end;
        }

        return result;
    }

    private static bool TryReadCompressed(byte[] buffer, ref int position, out int value)
    {
        value = 0;
        if (position >= buffer.Length) return false;
        var first = buffer[position];
        if ((first & 0x80) == 0)
        {
            value = first;
            position += 1;
            return true;
        }
        if ((first & 0xC0) == 0x80)
        {
            if (position + 1 >= buffer.Length) return false;
            value = ((first & 0x3F) << 8) | buffer[position + 1];
            position += 2;
            return true;
        }
        if ((first & 0xE0) == 0xC0)
        {
            if (position + 3 >= buffer.Length) return false;
            value = ((first & 0x1F) << 24) | (buffer[position + 1] << 16) |
                    (buffer[position + 2] << 8) | buffer[position + 3];
            position += 4;
            return true;
        }
        return false;
    }

    private static bool IsInterestingString(string value, Regex? filter)
    {
        if (filter is not null) return filter.IsMatch(value);

        return UrlPattern.IsMatch(value)
            || FileNamePattern.IsMatch(value)
            || WindowsPathPattern.IsMatch(value)
            || (value.Contains('{') && value.Contains('}'))
            || IdentifierPattern.IsMatch(value)
            || value.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves the entry-point token to "Namespace.Type.Method" when it names a method.</summary>
    private static string? DescribeEntryPoint(MetadataReader md, int token)
    {
        if ((token >> 24) != 0x06) return null; // only the MethodDef table can be resolved here

        var method = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF));
        var type = md.GetTypeDefinition(method.GetDeclaringType());
        var ns = md.GetString(type.Namespace);
        var typeName = md.GetString(type.Name);
        var name = md.GetString(method.Name);
        return (string.IsNullOrEmpty(ns) ? string.Empty : ns + ".") + typeName + "." + name;
    }

    private static bool IsFrameworkAssembly(string name) =>
        name.Equals("System", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
        || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);

    private static string RenderFileAnalysis(FileAnalysis analysis, int stringLimit)
    {
        var sb = new StringBuilder();
        sb.Append("file: ").Append(analysis.Path).Append((char)10);
        sb.Append("assembly: ").Append(analysis.AssemblyName);
        if (analysis.Version.Length > 0) sb.Append(' ').Append(analysis.Version);
        sb.Append(analysis.IsLibrary ? " (library)" : " (executable)");
        sb.Append(analysis.Signed ? ", strong-named" : string.Empty).Append((char)10);
        sb.Append("size: ").Append((analysis.SizeBytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" KB, ")
          .Append(analysis.TypeCount).Append(" types").Append((char)10);

        if (analysis.EntryPoints.Count > 0)
        {
            sb.Append("entry points: ").Append(string.Join(", ", analysis.EntryPoints)).Append((char)10);
        }

        sb.Append("dependencies (").Append(analysis.Dependencies.Count).Append(", non-framework):").Append((char)10);
        if (analysis.Dependencies.Count == 0)
        {
            sb.Append("  (none)").Append((char)10);
        }
        else
        {
            foreach (var dependency in analysis.Dependencies.Take(60))
            {
                sb.Append("  ").Append(dependency).Append((char)10);
            }
            if (analysis.Dependencies.Count > 60)
            {
                sb.Append("  … ").Append(analysis.Dependencies.Count - 60).Append(" more").Append((char)10);
            }
        }

        sb.Append("namespaces (").Append(analysis.Namespaces.Count).Append("):").Append((char)10);
        foreach (var pair in analysis.Namespaces.Take(40))
        {
            sb.Append("  ").Append(pair.Key).Append(" — ").Append(pair.Value).Append(" type(s)").Append((char)10);
        }
        if (analysis.Namespaces.Count > 40)
        {
            sb.Append("  … ").Append(analysis.Namespaces.Count - 40).Append(" more").Append((char)10);
        }

        if (analysis.Strings.Count > 0)
        {
            sb.Append("strings (").Append(analysis.Strings.Count).Append(", relevance-ranked):").Append((char)10);
            foreach (var value in analysis.Strings.Take(stringLimit))
            {
                sb.Append("  ").Append(value.Length > 160 ? value.Substring(0, 160) + "…" : value).Append((char)10);
            }
            if (analysis.Strings.Count > stringLimit)
            {
                sb.Append("  … ").Append(analysis.Strings.Count - stringLimit)
                  .Append(" more (raise strings_limit or narrow string_filter)").Append((char)10);
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ---------------- diff_assemblies ----------------

    private static Task<ToolOutcome> DiffAssembliesAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(args, context);
        var oldPath = ResolvePath(RequireString(args, "old_path"), workingDirectory);
        var newPath = ResolvePath(RequireString(args, "new_path"), workingDirectory);
        var limit = Math.Clamp(OptionalInt(args, "limit", 60), 5, 2000);

        if (!File.Exists(oldPath)) return Task.FromResult(Fail("Old assembly not found: " + oldPath));
        if (!File.Exists(newPath)) return Task.FromResult(Fail("New assembly not found: " + newPath));

        try
        {
            var before = ReadApiSurface(oldPath);
            var after = ReadApiSurface(newPath);
            return Task.FromResult(Truncate(RenderApiDiff(before, after, limit), context.MaxChars));
        }
        catch (BadImageFormatException)
        {
            return Task.FromResult(Fail("One of the files is not a .NET image."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.GetType().Name + ": " + OneLine(ex.Message)));
        }
    }

    /// <summary>Only the binary at the given path is read; nothing is loaded into the process.</summary>
    private static ApiSurface ReadApiSurface(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException("no CLI metadata");
        var md = pe.GetMetadataReader();

        var surface = new ApiSurface
        {
            Identity = md.IsAssembly
                ? "file " + Path.GetFileName(path) + ": " + md.GetString(md.GetAssemblyDefinition().Name) + " " +
                  md.GetAssemblyDefinition().Version
                : "file " + Path.GetFileName(path) + " (no manifest)",
        };

        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            var name = md.GetString(td.Name);
            if (name == "<Module>" || name.StartsWith("<", StringComparison.Ordinal)) continue;
            if ((td.Attributes & TypeAttributes.Public) == 0) continue;

            var ns = md.GetString(td.Namespace);
            var typeName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            var members = surface.Members[typeName] = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var methodHandle in td.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var methodName = md.GetString(method.Name);
                if (methodName.StartsWith("<", StringComparison.Ordinal)) continue;
                if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
                var parameters = method.GetParameters().Count;
                members.Add(
                    "method " + methodName + "/" + parameters +
                    ((method.Attributes & MethodAttributes.Static) != 0 ? " static" : string.Empty));
            }

            foreach (var fieldHandle in td.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public) continue;
                var fieldName = md.GetString(field.Name);
                if (fieldName.StartsWith("<", StringComparison.Ordinal)) continue;
                members.Add("field " + fieldName);
            }

            foreach (var propertyHandle in td.GetProperties())
            {
                members.Add("property " + md.GetString(md.GetPropertyDefinition(propertyHandle).Name));
            }

            foreach (var eventHandle in td.GetEvents())
            {
                members.Add("event " + md.GetString(md.GetEventDefinition(eventHandle).Name));
            }
        }

        return surface;
    }

    private static string RenderApiDiff(ApiSurface before, ApiSurface after, int limit)
    {
        var addedTypes = after.Members.Keys.Except(before.Members.Keys, StringComparer.Ordinal).ToArray();
        var removedTypes = before.Members.Keys.Except(after.Members.Keys, StringComparer.Ordinal).ToArray();

        var changed = new List<(string Type, string[] Added, string[] Removed)>();
        foreach (var type in before.Members.Keys.Intersect(after.Members.Keys, StringComparer.Ordinal))
        {
            var added = after.Members[type].Except(before.Members[type], StringComparer.Ordinal).ToArray();
            var removed = before.Members[type].Except(after.Members[type], StringComparer.Ordinal).ToArray();
            if (added.Length > 0 || removed.Length > 0) changed.Add((type, added, removed));
        }

        var breaking = removedTypes.Length > 0 || changed.Any(c => c.Removed.Length > 0);

        var sb = new StringBuilder();
        sb.Append("old: ").Append(before.Identity).Append((char)10);
        sb.Append("new: ").Append(after.Identity).Append((char)10);
        sb.Append("verdict: ").Append(breaking ? "breaking" : "compatible")
          .Append(" — types +").Append(addedTypes.Length).Append(" -").Append(removedTypes.Length)
          .Append(", ~").Append(changed.Count).Append((char)10);

        if (addedTypes.Length + removedTypes.Length + changed.Count == 0)
        {
            sb.Append("The public surface is identical.").Append((char)10);
            return sb.ToString().TrimEnd();
        }

        if (addedTypes.Length > 0)
        {
            sb.Append("added types (").Append(addedTypes.Length).Append("):").Append((char)10);
            foreach (var type in addedTypes.Take(limit)) sb.Append("  + ").Append(type).Append((char)10);
            if (addedTypes.Length > limit) sb.Append("  … ").Append(addedTypes.Length - limit).Append(" more").Append((char)10);
        }

        if (removedTypes.Length > 0)
        {
            sb.Append("removed types (").Append(removedTypes.Length).Append("):").Append((char)10);
            foreach (var type in removedTypes.Take(limit)) sb.Append("  - ").Append(type).Append((char)10);
            if (removedTypes.Length > limit) sb.Append("  … ").Append(removedTypes.Length - limit).Append(" more").Append((char)10);
        }

        if (changed.Count > 0)
        {
            sb.Append("changed types (").Append(changed.Count).Append("):").Append((char)10);
            foreach (var (type, added, removed) in changed.Take(limit))
            {
                sb.Append("  ~ ").Append(type).Append(": +").Append(added.Length).Append(" -").Append(removed.Length).Append((char)10);
                foreach (var member in added.Take(20)) sb.Append("      + ").Append(member).Append((char)10);
                foreach (var member in removed.Take(20)) sb.Append("      - ").Append(member).Append((char)10);
            }
            if (changed.Count > limit) sb.Append("  … ").Append(changed.Count - limit).Append(" more").Append((char)10);
        }

        return sb.ToString().TrimEnd();
    }

    // ---------------- attach_process ----------------

    private static Task<ToolOutcome> AttachProcessAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var requested = OptionalString(args, "process");
        var properties = OptionalString(args, "properties");
        var current = Process.GetCurrentProcess();

        Process[] candidates;
        if (requested is null || requested.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            candidates = new[] { current };
        }
        else if (int.TryParse(requested, out var pid))
        {
            try
            {
                candidates = new[] { Process.GetProcessById(pid) };
            }
            catch (ArgumentException)
            {
                return Task.FromResult(Fail("No process with PID " + pid + "."));
            }
        }
        else
        {
            var trimmed = requested.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? requested.Substring(0, requested.Length - 4)
                : requested;
            candidates = Process.GetProcessesByName(trimmed);
            if (candidates.Length == 0)
            {
                var similar = Process.GetProcesses()
                    .Select(SafeName)
                    .Where(n => n.Length > 0 && n.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(15)
                    .ToArray();
                return Task.FromResult(Fail(
                    "No running process named '" + trimmed + "'." +
                    (similar.Length > 0 ? " Similar: " + string.Join(", ", similar) : string.Empty)));
            }
        }

        try
        {
            var sb = new StringBuilder();
            foreach (var process in candidates.OrderBy(p => p.Id).Take(10))
            {
                var header = "pid " + process.Id + "  " + SafeName(process);
                if (process.Id == current.Id)
                {
                    header += "  (this MCP server — assemblies below are really loaded)";
                }
                sb.Append(header).Append((char)10);

                var modules = ReadModules(process);
                if (modules is null)
                {
                    sb.Append(
                        Environment.OSVersion.Platform == PlatformID.Win32NT
                            ? "    modules: unavailable (access denied - usually bitness or privilege)"
                            : "    modules: unavailable (this host cannot read /proc of that process)")
                        .Append((char)10).Append((char)10);
                    continue;
                }

                // The extension proves nothing: a process maps native DLLs next to managed ones,
                // so each mapped file is classified by its own headers.
                var described = modules.Select(DescribeModule).ToArray();
                var assemblies = described
                    .Where(d => d.Kind == "managed")
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var native = described.Count(d => d.Kind == "native");
                var unreadable = described.Count(d => d.Kind == "unreadable");
                var executables = described
                    .Where(d => d.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .Select(d => Path.GetFileName(d.Path))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                sb.Append("    modules: ").Append(described.Length)
                  .Append(" — ").Append(assemblies.Length).Append(" managed, ")
                  .Append(native).Append(" native");
                if (unreadable > 0) sb.Append(", ").Append(unreadable).Append(" unreadable");
                sb.Append((char)10);
                if (executables.Length > 0)
                {
                    sb.Append("    executables: ").Append(string.Join(", ", executables)).Append((char)10);
                }

                if (properties is not null)
                {
                    var matched = described
                        .Where(d => Path.GetFileName(d.Path).Contains(properties, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    sb.Append("    matching '").Append(properties).Append("':").Append((char)10);
                    if (matched.Length == 0) sb.Append("      (none)").Append((char)10);
                    foreach (var module in matched.Take(80)) AppendModule(sb, module);
                }
                else if (assemblies.Length == 0)
                {
                    sb.Append("    assemblies: none mapped; this process is not running managed code").Append((char)10);
                }
                else
                {
                    foreach (var module in assemblies.Take(200)) AppendModule(sb, module);
                    if (assemblies.Length > 200)
                    {
                        sb.Append("      … ").Append(assemblies.Length - 200).Append(" more").Append((char)10);
                    }
                }

                sb.Append((char)10);
            }

            sb.Append("Read-only snapshot of what a process currently has mapped; ");
            sb.Append("use inspect_assembly or analyze_file on a path to look inside one of those files.");
            return Task.FromResult(Truncate(sb.ToString().TrimEnd(), context.MaxChars));
        }
        finally
        {
            foreach (var process in candidates)
            {
                if (process.Id != current.Id) process.Dispose();
            }
        }
    }

    private static string[]? ReadModules(Process process)
    {
        try
        {
            return process.Modules
                .Cast<ProcessModule>()
                .Select(module => module.FileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static ModuleInfo DescribeModule(string modulePath)
    {
        try
        {
            using var stream = File.OpenRead(modulePath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new ModuleInfo(modulePath, "native", null, null);
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return new ModuleInfo(modulePath, "module", null, null);
            var def = md.GetAssemblyDefinition();
            return new ModuleInfo(modulePath, "managed", md.GetString(def.Name), def.Version.ToString());
        }
        catch
        {
            return new ModuleInfo(modulePath, "unreadable", null, null);
        }
    }

    private static void AppendModule(StringBuilder sb, ModuleInfo module)
    {
        sb.Append("      ");
        if (module.Name is not null)
        {
            sb.Append(module.Name);
            if (module.Version is not null) sb.Append(' ').Append(module.Version);
            sb.Append("  (").Append(Path.GetFileName(module.Path)).Append(')');
        }
        else
        {
            sb.Append(Path.GetFileName(module.Path)).Append("  (").Append(module.Kind).Append(')');
        }
        sb.Append((char)10);
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
