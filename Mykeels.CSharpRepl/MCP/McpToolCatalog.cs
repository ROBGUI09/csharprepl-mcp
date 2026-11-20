// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

/// <summary>Result of a single tool invocation, rendered as one MCP text content block.</summary>
public sealed record ToolOutcome(string Text, bool IsError = false);

public delegate Task<ToolOutcome> ToolHandler(
    JsonObject args,
    CSharpSession session,
    ToolContext context,
    CancellationToken cancellationToken
);

public sealed class ToolContext
{
    public int DefaultTimeoutMs { get; init; } = 30_000;
    public int MaxTimeoutMs { get; init; } = 600_000;
    public int MaxChars { get; init; } = 20_000;

    /// <summary>Fallback for calls that do not pass their own cwd.</summary>
    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;
}

public sealed record McpTool(
    string Name,
    string Title,
    string Description,
    string InputSchema,
    ToolHandler Handler
);

/// <summary>
/// The MCP tool surface. Descriptions are the model's only documentation, so each one states
/// what the tool does, the state it touches, and the failure mode it guards against.
/// </summary>
public static partial class McpTools
{
    public static IReadOnlyList<McpTool> All { get; } = new List<McpTool>
    {
        new(
            Name: "run_script",
            Title: "Run C# in a persistent REPL session",
            Description:
                "Evaluates C# in a live Roslyn scripting session. State persists across calls: variables, " +
                "types, usings, #r references and loaded assemblies all survive, so you can build up a program " +
                "incrementally. The value of a final expression written WITHOUT a trailing semicolon is returned " +
                "(use 'return expr;' for an explicit value). Console output of the snippet is captured and " +
                "returned instead of being written to the protocol stream. Long snippets must finish inside " +
                "timeout_ms; a timeout cancels the evaluation and may leave state half-applied.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "code": {
                      "type": "string",
                      "description": "C# script code: statements, expressions, #r/#load directives, await is allowed."
                    },
                    "save_as": {
                      "type": "string",
                      "description": "Keep the returned object under this name for get_result, so a later call reads it instead of rebuilding it."
                    },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "timeout_ms": {
                      "type": "integer",
                      "description": "Cancellation budget in milliseconds (default 30000)."
                    },
                    "max_chars": {
                      "type": "integer",
                      "description": "Truncate the tool result to this many characters (default 20000)."
                    }
                  },
                  "required": ["code"]
                }
                """,
            Handler: RunScriptAsync
        ),
        new(
            Name: "run_file",
            Title: "Run a .csx/.cs file",
            Description:
                "Runs a C# script file (.csx or .cs) in the same persistent session as run_script, so a file " +
                "can consume variables and references established earlier. The working directory is switched to " +
                "the file's folder for the duration of the run, which makes relative #r \"lib.dll\" and relative " +
                "file IO resolve next to the script. Optional args are exposed to the script as args / Args.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path to the .csx/.cs file (relative paths resolve against cwd)." },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "args": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Arguments exposed to the script as args / Args."
                    },
                    "timeout_ms": { "type": "integer", "description": "Cancellation budget in milliseconds (default 30000)." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": ["path"]
                }
                """,
            Handler: RunFileAsync
        ),
        new(
            Name: "add_reference",
            Title: "Preload assemblies (DLLs)",
            Description:
                "Makes existing .NET assemblies available to the session: adds #r compile-time references and " +
                "loads them into the process so reflection, typeof(...) and direct calls work in later run_script " +
                "calls. Accepts DLLs or folders (folders contribute their *.dll, optionally recursive). Use this " +
                "before touching a locally built library, a plugin assembly, or a decompiled target. Assemblies " +
                "with unmanaged dependencies or a .deps.json may still fail to resolve at first use.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "paths": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "DLL files or folders to load."
                    },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "recursive": {
                      "type": "boolean",
                      "description": "When a folder is given, include subfolders (default false)."
                    }
                  },
                  "required": ["paths"]
                }
                """,
            Handler: AddReferenceAsync
        ),
        new(
            Name: "add_package",
            Title: "Pull a NuGet package into the session",
            Description:
                "Downloads a NuGet package from nuget.org, extracts the best-matching lib/ folder for this " +
                "runtime and adds those assemblies as references. Best effort: transitive dependencies are not " +
                "resolved automatically, so add them explicitly with add_package or add_reference if types fail " +
                "to load. Requires network access.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "description": "Package id, e.g. Newtonsoft.Json." },
                    "version": { "type": "string", "description": "Exact version; omit for the latest stable release." }
                  },
                  "required": ["id"]
                }
                """,
            Handler: AddPackageAsync
        ),
        new(
            Name: "add_project",
            Title: "Build and reference a .NET project",
            Description:
                "Build a .csproj (or the single project in a directory) and reference its output assembly, so its " +
                "types can be used by run_script. Use this instead of add_reference when you only have a project " +
                "file and want the current build output. Pass build=false to skip dotnet build and just resolve " +
                "the last TargetPath. Builds can take a while; raise timeout_ms for large solutions.",
            InputSchema: """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path to a .csproj, or a directory containing exactly one .csproj." },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "configuration": { "type": "string", "description": "Build configuration, Debug by default." },
                    "build": { "type": "boolean", "description": "Run dotnet build first (default true)." }
                  },
                  "required": ["path"]
                }
                """,
            Handler: AddProjectAsync
        ),
        new(
            Name: "run_tests",
            Title: "Run dotnet test and report failures",
            Description:
                "Builds and runs the tests of a project, solution or directory, then summarizes the outcome: " +
                "counts, the names of failing tests and the tail of the runner output. A failing test is a result, " +
                "not a tool error; only a run that never produced results (build failure, missing SDK, timeout) " +
                "is an error. Test hosts are separate processes, so a restricted host that forbids process " +
                "inspection may refuse to run them at all.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "A .csproj, .sln or a directory containing one." },
                    "filter": { "type": "string", "description": "dotnet test --filter expression, e.g. FullyQualifiedName~Parser." },
                    "configuration": { "type": "string", "description": "Build configuration, Debug by default." },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "timeout_ms": { "type": "integer", "description": "Run budget in milliseconds (default 300000)." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": ["path"]
                }
                """,
            Handler: RunTestsAsync
        ),
        new(
            Name: "list_types",
            Title: "Search types across loaded assemblies",
            Description:
                "Regex-searches type names in every assembly loaded in the session (plus anything added with " +
                "add_reference). Use it to discover what an unfamiliar DLL exposes, or to recover the exact " +
                "namespace of a type before describe_type / decompile.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "pattern": { "type": "string", "description": "Regex matched against the full type name (case-insensitive). Default: all." },
                    "namespace": { "type": "string", "description": "Only types whose namespace starts with this prefix." },
                    "limit": { "type": "integer", "description": "Max results (default 100)." }
                  },
                  "required": []
                }
                """,
            Handler: ListTypesAsync
        ),
        new(
            Name: "describe_type",
            Title: "List the members of a type",
            Description:
                "Shows the shape of a loaded type: base type, interfaces, constructors, methods, properties, " +
                "fields and events with signatures. Cheaper and more precise than decompiling when you only " +
                "need the callable surface. Resolve by full name, then simple name.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "type": { "type": "string", "description": "Type full name, or a unique simple name." },
                    "include_non_public": { "type": "boolean", "description": "Include private/internal members (default false)." }
                  },
                  "required": ["type"]
                }
                """,
            Handler: DescribeTypeAsync
        ),
        new(
            Name: "inspect_assembly",
            Title: "Inspect an assembly without executing it",
            Description:
                "Reads PE/CLI metadata only - the process never loads or runs the file. Reports identity, " +
                "version, entry point, referenced assemblies and public types. This is the safe first step when " +
                "the DLL is untrusted or has unknown side effects (module initializers run on load).",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path to the .dll/.exe." },
                    "types_limit": { "type": "integer", "description": "Max public types to list (default 200)." }
                  },
                  "required": ["path"]
                }
                """,
            Handler: InspectAssemblyAsync
        ),
        new(
            Name: "decompile",
            Title: "Decompile a type to C#",
            Description:
                "Decompiles a type from a .NET assembly to readable C# using ICSharpCode.Decompiler. Give path " +
                "explicitly, or only a type name already loaded in the session and the assembly is located " +
                "automatically. Optional member narrows the output to one method/property/field block. Output " +
                "is truncated - decompile one type at a time.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "type": { "type": "string", "description": "Full type name, e.g. System.String or My.Ns.Widget." },
                    "path": { "type": "string", "description": "Assembly path; omit to use the assembly of a loaded type." },
                    "member": { "type": "string", "description": "Optional member name to extract from the decompiled type." },
                    "max_chars": { "type": "integer", "description": "Truncate output (default 20000)." }
                  },
                  "required": ["type"]
                }
                """,
            Handler: DecompileAsync
        ),
        new(
            Name: "analyze_file",
            Title: "Triage a binary: strings, dependencies, namespaces",
            Description:
                "Reads a .NET binary without loading it and reports what triage needs: identity, size, entry " +
                "points, non-framework dependencies, how types distribute across namespaces, and the strings in " +
                "the metadata heap - URLs, config keys, file names, SQL, format templates - with library noise " +
                "filtered out. Use it to decide what is worth decompiling, or to find an entry point.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path to the .dll/.exe (relative paths resolve against cwd)." },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "strings": { "type": "boolean", "description": "Include metadata strings (default true)." },
                    "string_filter": { "type": "string", "description": "Regex that replaces the default relevance ranking and keeps only matching strings." },
                    "strings_limit": { "type": "integer", "description": "Max strings to list (default 80)." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": ["path"]
                }
                """,
            Handler: AnalyzeFileAsync
        ),
        new(
            Name: "diff_assemblies",
            Title: "Compare the public API of two assembly versions",
            Description:
                "Diffs the public surface of two builds of the same library from metadata only: added and " +
                "removed types, plus added and removed members inside types that exist in both. Reports " +
                "'breaking' when something disappeared, which answers whether an upgrade is safe.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "old_path": { "type": "string", "description": "The reference build (relative paths resolve against cwd)." },
                    "new_path": { "type": "string", "description": "The build to compare against it." },
                    "cwd": {
                      "type": "string",
                      "description": "Working directory for this call only: absolute, or relative to the server default. %VAR% is expanded. Relative paths in the call resolve against it."
                    },
                    "limit": { "type": "integer", "description": "Max entries listed per section (default 60)." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": ["old_path", "new_path"]
                }
                """,
            Handler: DiffAssembliesAsync
        ),
        new(
            Name: "list_references",
            Title: "Show what the session can see",
            Description:
                "Lists the assemblies currently loaded or referenced in the session, with their locations. Use it " +
                "after add_reference to confirm a load actually happened.",
            InputSchema: """{ "type": "object", "properties": {}, "required": [] }""",
            Handler: ListReferencesAsync
        ),
        new(
            Name: "attach_process",
            Title: "Inspect the modules of a running process",
            Description:
                "Reports what a process currently has mapped: how many modules, which of them are managed " +
                "assemblies with their versions, which are native, and its executables. With properties, " +
                "matched modules are listed instead. " +
                "Read-only - it attaches no debugger and injects nothing - and a 32-bit target can only be " +
                "read from a 32-bit host, so an access-denied result usually means bitness or privilege.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "process": { "type": "string", "description": "Process name, PID, or \"current\" for this server (default current)." },
                    "properties": { "type": "string", "description": "Only modules whose file name contains this substring, resolved to assembly identity." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": []
                }
                """,
            Handler: AttachProcessAsync
        ),
        new(
            Name: "reset_session",
            Title: "Start a clean session",
            Description:
                "Discards the current script state (variables, defined types, usings) and the recorded reference " +
                "list, then lazily builds a fresh Roslyn session on the next call. Assemblies already loaded into " +
                "the process stay loaded, so a reset also needs add_reference again for a clean compile surface.",
            InputSchema: """{ "type": "object", "properties": {}, "required": [] }""",
            Handler: ResetSessionAsync
        ),
        new(
            Name: "history",
            Title: "Recent submissions",
            Description: "Returns the most recent submissions evaluated in this session, oldest first.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "limit": { "type": "integer", "description": "How many to return (default 10)." }
                  },
                  "required": []
                }
                """,
            Handler: HistoryAsync
        ),
        new(
            Name: "session_state",
            Title: "Variables currently in scope",
            Description:
                "Lists the variables and their types that survived from earlier submissions in this session. Use it " +
                "when a value you expected to still be defined is missing, or to re-derive the exact type of " +
                "something you built a few calls ago.",
            InputSchema: """{ "type": "object", "properties": {}, "required": [] }""",
            Handler: SessionStateAsync
        ),
        new(
            Name: "get_result",
            Title: "Read a stored result by name",
            Description:
                "Prints an object kept by run_script save_as, formatted the way the REPL prints it. Use it to " +
                "look at a value or collection between calls without re-running the code that built it; the " +
                "object is the live one, so later mutations are visible.",
            InputSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "The name passed to save_as." },
                    "drop": { "type": "boolean", "description": "Forget the handle after reading it (default false)." },
                    "max_chars": { "type": "integer", "description": "Truncate the tool result (default 20000)." }
                  },
                  "required": ["name"]
                }
                """,
            Handler: GetResultAsync
        ),
        new(
            Name: "list_results",
            Title: "List stored result handles",
            Description:
                "Names and types of the objects saved with save_as in this session, oldest first. " +
                "reset_session clears them.",
            InputSchema: """{ "type": "object", "properties": {}, "required": [] }""",
            Handler: ListResultsAsync
        ),
    }.AsReadOnly();

    public static McpTool? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    // ---------- argument helpers ----------

    internal static string RequireString(JsonObject args, string name)
    {
        if (args[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        throw new ArgumentException("Missing required string argument '" + name + "'.");
    }

    internal static string? OptionalString(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    internal static bool OptionalBool(JsonObject args, string name, bool fallback = false) =>
        args[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    internal static int OptionalInt(JsonObject args, string name, int fallback)
    {
        if (args[name] is not JsonValue value) return fallback;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<double>(out var d)) return (int)d;
        if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return fallback;
    }

    internal static IReadOnlyList<string> OptionalStringArray(JsonObject args, string name)
    {
        if (args[name] is not JsonArray array) return Array.Empty<string>();
        return array
            .Select(node => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray();
    }

    internal static ToolOutcome Truncate(string text, int maxChars) =>
        text.Length <= maxChars
            ? new ToolOutcome(text)
            : new ToolOutcome(text.Substring(0, maxChars) + "\n… (truncated at " + maxChars + " chars)");

    internal static ToolOutcome Fail(string message) => new("ERROR: " + message, true);
}
