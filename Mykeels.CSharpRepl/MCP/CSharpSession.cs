// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpRepl.Services;
using CSharpRepl.Services.Logging;
using CSharpRepl.Services.Roslyn;
using CSharpRepl.Services.Roslyn.Formatting;
using CSharpRepl.Services.Roslyn.Scripting;
using Microsoft.CodeAnalysis;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Mykeels.CSharpRepl.Mcp;

/// <summary>
/// Thread-safe capture of everything the engine writes to <see cref="Console.Out"/> while a
/// snippet runs, so a tool call can report the snippet's console output instead of leaking it.
/// </summary>
public sealed class CaptureTextWriter : TextWriter
{
    private readonly StringBuilder buffer = new();
    private readonly object gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public void Clear() { lock (gate) { buffer.Clear(); } }

    public string Drain()
    {
        lock (gate)
        {
            var text = buffer.ToString();
            buffer.Clear();
            return text;
        }
    }

    public override void Write(char value) { lock (gate) { buffer.Append(value); } }

    public override void Write(string? value) { lock (gate) { buffer.Append(value); } }

    public override void WriteLine(string? value) { lock (gate) { buffer.Append(value).Append('\n'); } }
}

/// <summary>Silences the engine's trace logging (MCP must not write to stdout).</summary>
internal sealed class SilentTraceLogger : ITraceLogger
{
    public void Log(string message) { }
    public void Log(Func<string> message) { }
    public void LogPaths(string message, Func<IEnumerable<string?>> paths) { }
}

/// <summary>Structured outcome of one evaluation.</summary>
public sealed record EvalOutcome(
    bool IsError,
    string? ValueText,
    string? ValueType,
    string? ValueJson,
    string Stdout,
    string? Error,
    string? Diagnostics,
    long ElapsedMs
)
{
    /// <summary>The evaluated object itself, when the snippet produced one; used by result handles.</summary>
    public object? Value { get; init; }
}

/// <summary>
/// Owns one long-lived <see cref="RoslynServices"/> instance so script state (variables, types,
/// usings, loaded assemblies) survives between tool calls — the whole point of a REPL-backed tool.
/// </summary>
public sealed class CSharpSession
{
    private readonly object gate = new();
    private readonly CaptureTextWriter capture;
    private readonly string[] defaultUsings;
    private readonly string[] initialReferences;
    private readonly List<string> references = new();
    private readonly List<string> submissions = new();
    private readonly Dictionary<string, ResultHandle> handles = new(StringComparer.Ordinal);
    private RoslynServices? services;

    public CSharpSession(
        CaptureTextWriter capture,
        IEnumerable<string>? defaultUsings = null,
        IEnumerable<string>? references = null)
    {
        this.capture = capture;
        this.defaultUsings = defaultUsings?.ToArray() ?? Array.Empty<string>();
        this.initialReferences = (references ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> References
    {
        get { lock (gate) { return references.ToArray(); } }
    }

    public string RuntimeDescription
    {
        get
        {
            var rt = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            return rt + " | " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        }
    }

    private RoslynServices EnsureServices()
    {
        lock (gate)
        {
            if (services is not null) return services;

            var config = new Configuration(
                references: initialReferences.Length == 0 ? null : initialReferences,
                usings: defaultUsings.Length == 0 ? null : defaultUsings,
                useTerminalPaletteTheme: true, // avoids probing for a theme file on disk
                applicationName: "csharprepl-mcp",
                logSuccess: (_, _) => { },
                logError: (_, _, _) => { }
            );

            var console = new SystemConsoleEx();
            services = new RoslynServices(console, config, new SilentTraceLogger());
            return services;
        }
    }

    /// <summary>Forces Roslyn/reference warm-up so the first real tool call is fast.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        var svc = EnsureServices();
        await svc.WarmUpAsync(Array.Empty<string>()).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Roslyn's interactive loader resolves <c>#r</c> files lazily, at execution time. A host with a
    /// restricted token can fail that late image load (FileLoadException: access denied) even though the file
    /// is readable, so each tracked reference is loaded into the process up front — after which submissions
    /// bind to an assembly that already exists and nothing touches the file again.
    /// </summary>
    public void EnsureReferencesLoaded()
    {
        string[] snapshot;
        lock (gate)
        {
            snapshot = references.ToArray();
        }

        foreach (var path in snapshot)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var name = AssemblyName.GetAssemblyName(path).Name;
                if (
                    name is not null
                    && AppDomain
                        .CurrentDomain.GetAssemblies()
                        .Any(a =>
                            string.Equals(
                                a.GetName().Name,
                                name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                )
                {
                    continue;
                }

                Assembly.LoadFrom(path);
            }
            catch
            {
                // Best effort: metadata-only or already-resolved references must not break the call.
            }
        }
    }

    public void TrackReference(string path)
    {
        lock (gate)
        {
            if (!references.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(path);
            }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            services = null;
            references.Clear();
            submissions.Clear();
            handles.Clear();
        }
    }

    /// <summary>An object kept alive by name, so a later call can read it instead of re-running the code.</summary>
    public sealed record ResultHandle(string Name, object? Value, string? TypeName, DateTimeOffset SavedAt);

    public IReadOnlyList<ResultHandle> Handles
    {
        get
        {
            lock (gate)
            {
                return handles.Values.OrderBy(h => h.Name, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public void SaveHandle(string name, object? value, string? typeName)
    {
        lock (gate)
        {
            handles[name] = new ResultHandle(name, value, typeName, DateTimeOffset.Now);
        }
    }

    public bool TryGetHandle(string name, out ResultHandle handle)
    {
        lock (gate)
        {
            if (handles.TryGetValue(name, out var found))
            {
                handle = found;
                return true;
            }
        }

        handle = null!;
        return false;
    }

    public bool DropHandle(string name)
    {
        lock (gate)
        {
            return handles.Remove(name);
        }
    }

    /// <summary>Formats a stored value with the same pretty-printer the REPL uses.</summary>
    public async Task<string> RenderValueAsync(object? value, CancellationToken cancellationToken)
    {
        var svc = EnsureServices();
        return await FormatValueAsync(svc, value).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Variables that survived from earlier submissions, newest state.</summary>
    public async Task<IReadOnlyList<ScriptVariableSnapshot>> GetVariablesAsync(CancellationToken cancellationToken)
    {
        var svc = EnsureServices();
        return await svc.GetVariablesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> GetHistoryAsync()
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(submissions.ToArray());
        }
    }

    /// <summary>Evaluates <paramref name="code"/> in the persistent submission chain.</summary>
    public async Task<EvalOutcome> EvaluateAsync(
        string code,
        string[]? args,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var svc = EnsureServices();
        capture.Clear();
        lock (gate)
        {
            submissions.Add(code);
            if (submissions.Count > 200) submissions.RemoveAt(0);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }

        var started = Environment.TickCount64;
        EvaluationResult result;
        try
        {
            result = await svc.EvaluateAsync(code, args, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EvalOutcome(
                true, null, null, null, capture.Drain(),
                $"Evaluation cancelled after {timeout.TotalMilliseconds:0} ms. " +
                "Raise timeout_ms or move long work into a background thread inside the script.",
                null, Environment.TickCount64 - started);
        }

        var elapsed = Environment.TickCount64 - started;
        var stdout = capture.Drain();

        switch (result)
        {
            case EvaluationResult.Success success:
            {
                string? valueText = null;
                string? valueType = null;
                string? valueJson = null;
                if (success.ReturnValue.HasValue)
                {
                    var value = success.ReturnValue.Value;
                    valueType = value?.GetType().FullName ?? "null";
                    valueText = await FormatValueAsync(svc, value).ConfigureAwait(false);
                    valueJson = TryJson(value);
                }
                return new EvalOutcome(false, valueText, valueType, valueJson, stdout, null, null, elapsed)
                {
                    Value = success.ReturnValue.HasValue ? success.ReturnValue.Value : null,
                };
            }
            case EvaluationResult.Error error:
            {
                var ex = error.Exception;
                var message = $"{ex.GetType().Name}: {Trim(ex.Message, 4000)}";
                var diagnostics = ExtractDiagnostics(ex);
                return new EvalOutcome(true, null, null, null, stdout, message, diagnostics, elapsed);
            }
            case EvaluationResult.Cancelled:
                return new EvalOutcome(true, null, null, null, stdout,
                    $"Evaluation cancelled after {elapsed} ms (timeout or client cancellation).", null, elapsed);
            default:
                return new EvalOutcome(true, null, null, null, stdout, "Unknown evaluation result type.", null, elapsed);
        }
    }

    private static string? ExtractDiagnostics(Exception ex)
    {
        // Roslyn surfaces compile errors as CompilationErrorException whose Message already lists them,
        // but Diagnostics carries severity/codes/locations that are useful in a tool result.
        if (ex is Microsoft.CodeAnalysis.Scripting.CompilationErrorException cee && cee.Diagnostics.Length > 0)
        {
            return string.Join("\n", cee.Diagnostics.Take(50).Select(d => d.ToString()));
        }
        return null;
    }

    private static async Task<string> FormatValueAsync(RoslynServices svc, object? value)
    {
        if (value is null) return "null";
        try
        {
            var renderable = await svc.PrettyPrintAsync(value, Level.FirstSimple).ConfigureAwait(false);
            return Render(renderable).TrimEnd();
        }
        catch (Exception ex)
        {
            return $"<formatting failed: {ex.Message}> {value}";
        }
    }

    /// <summary>Renders a Spectre renderable to plain text without ANSI codes.</summary>
    public static string Render(IRenderable renderable, int width = 200)
    {
        var writer = new StringWriter();
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        try { ansi.Profile.Width = width; } catch { /* profile may be fixed */ }
        ansi.Write(renderable);
        return writer.ToString();
    }

    private static string? TryJson(object? value)
    {
        if (value is null) return "null";
        var t = value.GetType();
        var simple = t.IsPrimitive || value is string || value is decimal || value is DateTime
            || value is DateTimeOffset || value is Guid || value is TimeSpan || t.IsEnum;
        if (!simple) return null;
        try
        {
            var json = JsonSerializer.Serialize(value, t);
            return json.Length <= 2000 ? json : null;
        }
        catch { return null; }
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "\n… (truncated)";
}
