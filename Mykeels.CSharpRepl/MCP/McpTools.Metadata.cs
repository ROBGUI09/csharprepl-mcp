// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{
    private static Task<ToolOutcome> InspectAssemblyAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(RequireString(args, "path"));
        if (!File.Exists(full)) return Task.FromResult(Fail("Assembly not found: " + full));
        var typesLimit = Math.Clamp(OptionalInt(args, "types_limit", 200), 1, 5000);

        try
        {
            using var stream = File.OpenRead(full);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return Task.FromResult(Fail("Not a managed assembly (no CLI metadata): " + full));
            var md = pe.GetMetadataReader();

            var sb = new StringBuilder();
            if (md.IsAssembly)
            {
                var def = md.GetAssemblyDefinition();
                var culture = md.GetString(def.Culture);
                sb.Append("assembly: ").Append(md.GetString(def.Name)).Append(' ').Append(def.Version).Append('\n');
                sb.Append("culture: ").Append(string.IsNullOrEmpty(culture) ? "neutral" : culture).Append('\n');
            }
            else
            {
                sb.Append("module: ").Append(md.GetString(md.GetModuleDefinition().Name)).Append(" (no assembly manifest)\n");
            }

            var entry = pe.PEHeaders.CorHeader is null ? 0 : pe.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
            sb.Append("entry point: ").Append(entry != 0 ? "yes (token 0x" + entry.ToString("X8") + ")" : "none (library)").Append('\n');
            sb.Append("dll: ").Append(pe.PEHeaders.IsDll ? "yes" : "no").Append('\n');

            var refs = md.AssemblyReferences
                .Select(h => md.GetAssemblyReference(h))
                .Select(r => md.GetString(r.Name) + " " + r.Version)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            sb.Append("references (").Append(refs.Length).Append("):\n");
            foreach (var reference in refs) sb.Append("  ").Append(reference).Append('\n');

            var allTypes = 0;
            var publicTypes = new List<string>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                allTypes++;
                if ((td.Attributes & TypeAttributes.Public) == 0) continue;
                var ns = md.GetString(td.Namespace);
                var typeName = md.GetString(td.Name);
                publicTypes.Add(string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName);
            }

            sb.Append("types: ").Append(allTypes).Append(" total, ").Append(publicTypes.Count).Append(" public\n");
            foreach (var t in publicTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(typesLimit))
            {
                sb.Append("  ").Append(t).Append('\n');
            }
            if (publicTypes.Count > typesLimit) sb.Append("  … ").Append(publicTypes.Count - typesLimit).Append(" more\n");

            return Task.FromResult(Truncate(sb.ToString(), context.MaxChars));
        }
        catch (BadImageFormatException)
        {
            return Task.FromResult(Fail("Not a valid .NET image: " + full));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.GetType().Name + ": " + OneLine(ex.Message)));
        }
    }

    private static Task<ToolOutcome> DecompileAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var typeName = RequireString(args, "type");
        var path = OptionalString(args, "path");
        var member = OptionalString(args, "member");
        var maxChars = Math.Clamp(OptionalInt(args, "max_chars", context.MaxChars), 500, context.MaxChars);

        if (path is null)
        {
            var resolved = ResolveType(typeName);
            if (resolved is null) return Task.FromResult(Fail("Type not loaded: " + typeName + ". Pass 'path' explicitly."));
            if (string.IsNullOrEmpty(resolved.Assembly.Location))
            {
                return Task.FromResult(Fail("Assembly for " + typeName + " has no file location (dynamic or in-memory)."));
            }
            path = resolved.Assembly.Location;
            typeName = resolved.FullName ?? typeName;
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return Task.FromResult(Fail("Assembly not found: " + full));

        try
        {
            var resolver = new ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver(full, false, null);
            var decompiler = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(
                full, resolver, new ICSharpCode.Decompiler.DecompilerSettings());

            string code;
            try
            {
                code = decompiler.DecompileTypeAsString(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(typeName));
            }
            catch (Exception ex)
            {
                var leaf = typeName.Split('.').Last();
                var candidates = decompiler.TypeSystem.MainModule.TopLevelTypeDefinitions
                    .Select(t => t.FullName is null ? t.Name : t.FullName.ToString())
                    .Where(n => n.Contains(leaf, StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .ToArray();
                return Task.FromResult(Fail("Could not decompile '" + typeName + "': " + OneLine(ex.Message) +
                    (candidates.Length > 0 ? "\nCandidates: " + string.Join(", ", candidates) : string.Empty)));
            }

            if (member is not null)
            {
                var extracted = ExtractMember(code, member);
                if (extracted is null)
                {
                    return Task.FromResult(Fail("Member '" + member + "' not found in decompiled " + typeName + "."));
                }
                code = extracted;
            }

            var header = "// " + Path.GetFileName(full) + " :: " + typeName +
                         (member is null ? string.Empty : "." + member) + "\n";
            return Task.FromResult(Truncate(header + code, maxChars));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.GetType().Name + ": " + OneLine(ex.Message)));
        }
    }

    private static string? ExtractMember(string code, string member)
    {
        var regex = new Regex("(?<![A-Za-z0-9_.])" + Regex.Escape(member) + "(?![A-Za-z0-9_])");
        var match = regex.Match(code);
        if (!match.Success) return null;

        var start = code.LastIndexOf('\n', Math.Max(0, match.Index - 1));
        start = start < 0 ? 0 : start + 1;

        var braceStart = code.IndexOf('{', match.Index);
        if (braceStart < 0) return code.Substring(start).TrimEnd();

        var depth = 0;
        for (var i = braceStart; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0) return code.Substring(start, i - start + 1).TrimEnd();
            }
        }
        return code.Substring(start).TrimEnd();
    }

    private static Task<ToolOutcome> ListReferencesAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var tracked = session.References;
        sb.Append("session references (").Append(tracked.Count).Append("):\n");
        foreach (var reference in tracked) sb.Append("  ").Append(reference).Append('\n');

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a =>
            {
                try
                {
                    var name = a.GetName();
                    return (Name: name.Name ?? "?", Version: name.Version is null ? "?" : name.Version.ToString(),
                        Location: string.IsNullOrEmpty(a.Location) ? "(in memory)" : a.Location);
                }
                catch
                {
                    return (Name: "?", Version: "?", Location: "?");
                }
            })
            .Where(a => !a.Name.StartsWith("System.", StringComparison.Ordinal) && a.Name != "System.Private.CoreLib")
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        sb.Append("\nloaded assemblies (").Append(assemblies.Length).Append(", System.* hidden):\n");
        foreach (var assembly in assemblies.Take(120))
        {
            sb.Append("  ").Append(assembly.Name).Append(' ').Append(assembly.Version)
              .Append("  ").Append(assembly.Location).Append('\n');
        }
        if (assemblies.Length > 120) sb.Append("  … ").Append(assemblies.Length - 120).Append(" more\n");

        return Task.FromResult(Truncate(sb.ToString(), context.MaxChars));
    }

    private static IEnumerable<(string Assembly, Type Type)> LoadedTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            }
            catch
            {
                continue;
            }

            var name = assembly.GetName().Name ?? "?";
            foreach (var type in types)
            {
                yield return (name, type);
            }
        }
    }

    private static Type? ResolveType(string name)
    {
        var candidates = LoadedTypes().Select(x => x.Type).ToArray();

        var exact = candidates.FirstOrDefault(t => string.Equals(t.FullName, name, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var ignoringCase = candidates.FirstOrDefault(t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (ignoringCase is not null) return ignoringCase;

        var simple = candidates.Where(t => string.Equals(t.Name, name, StringComparison.Ordinal)).ToArray();
        if (simple.Length == 1) return simple[0];

        var bySuffix = candidates.Where(t => t.FullName is not null && t.FullName.EndsWith("." + name, StringComparison.Ordinal)).ToArray();
        if (bySuffix.Length == 1) return bySuffix[0];

        return Type.GetType(name, throwOnError: false);
    }

    private static string Short(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf((char)96);
            if (tick > 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Short)) + ">";
        }
        if (type.IsArray) return Short(type.GetElementType() ?? typeof(object)) + "[]";
        if (type.IsByRef) return "ref " + Short(type.GetElementType() ?? typeof(object));
        if (type == typeof(void)) return "void";
        return type.Name;
    }

    private static string CtorSig(ConstructorInfo ctor, Type declaring)
    {
        var parameters = string.Join(", ", ctor.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name));
        return (ctor.IsStatic ? "static " : string.Empty) + Short(declaring) + "(" + parameters + ")";
    }

    private static string MethodSig(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name));
        return (method.IsStatic ? "static " : string.Empty) + Short(method.ReturnType) + " " + method.Name + "(" + parameters + ")";
    }

    private static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 300 ? line : line.Substring(0, 300) + "…";
    }
}
