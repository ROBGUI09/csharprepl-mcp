// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{

    private static Task<ToolOutcome> ListTypesAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var pattern = OptionalString(args, "pattern");
        var ns = OptionalString(args, "namespace");
        var limit = Math.Clamp(OptionalInt(args, "limit", 100), 1, 2000);

        Regex regex;
        try
        {
            regex = new Regex(pattern ?? ".*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(Fail("Invalid regex: " + ex.Message));
        }

        var matches = new List<string>();
        foreach (var (assemblyName, type) in LoadedTypes())
        {
            if (type.FullName is null) continue;
            if (ns is not null && !(type.Namespace ?? string.Empty).StartsWith(ns, StringComparison.OrdinalIgnoreCase)) continue;
            if (!regex.IsMatch(type.FullName) && !regex.IsMatch(type.Name)) continue;
            matches.Add(type.FullName + "  [" + assemblyName + "]");
        }

        if (matches.Count == 0) return Task.FromResult(new ToolOutcome("No matching types in loaded assemblies."));

        var ordered = matches.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var sb = new StringBuilder();
        foreach (var line in ordered.Take(limit)) sb.Append(line).Append('\n');
        if (ordered.Length > limit) sb.Append("… ").Append(ordered.Length - limit).Append(" more (raise limit).\n");
        sb.Append(ordered.Length).Append(" match(es).");
        return Task.FromResult(Truncate(sb.ToString(), context.MaxChars));
    }

    private static Task<ToolOutcome> DescribeTypeAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var name = RequireString(args, "type");
        var includeNonPublic = OptionalBool(args, "include_non_public");
        var type = ResolveType(name);
        if (type is null) return Task.FromResult(Fail("Type not found in loaded assemblies: " + name));

        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if (includeNonPublic) flags |= BindingFlags.NonPublic;

        var sb = new StringBuilder();
        sb.Append("type: ").Append(type.FullName).Append('\n');
        sb.Append("assembly: ").Append(type.Assembly.GetName().Name).Append(' ')
          .Append(type.Assembly.GetName().Version).Append('\n');
        if (type.BaseType is not null) sb.Append("base: ").Append(Short(type.BaseType)).Append('\n');
        var interfaces = type.GetInterfaces();
        if (interfaces.Length > 0)
        {
            sb.Append("interfaces: ").Append(string.Join(", ", interfaces.Select(t => Short(t)))).Append('\n');
        }
        if (type.IsAbstract && type.IsSealed) sb.Append("static class\n");

        void Section(string title, IEnumerable<string> lines)
        {
            var list = lines.ToArray();
            if (list.Length == 0) return;
            sb.Append(title).Append(":\n");
            foreach (var line in list) sb.Append("  ").Append(line).Append('\n');
        }

        Section("constructors", type.GetConstructors(flags).Select(c => CtorSig(c, type)));
        Section("methods", type.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => MethodSig(m)));
        Section("properties", type.GetProperties(flags).Select(p =>
            (p.GetMethod is not null && p.GetMethod.IsStatic ? "static " : string.Empty) + Short(p.PropertyType) + " " + p.Name +
            " { " + (p.CanRead ? "get; " : string.Empty) + (p.CanWrite ? "set; " : string.Empty) + "}"));
        Section("fields", type.GetFields(flags).Select(f =>
            (f.IsStatic ? "static " : string.Empty) + (f.IsInitOnly ? "readonly " : string.Empty) + Short(f.FieldType) + " " + f.Name));
        Section("events", type.GetEvents(flags).Select(e =>
            "event " + Short(e.EventHandlerType ?? typeof(object)) + " " + e.Name));

        return Task.FromResult(Truncate(sb.ToString(), context.MaxChars));
    }
}
