// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{
    private static async Task<ToolOutcome> RunScriptAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var code = RequireString(args, "code");
        var saveAs = OptionalString(args, "save_as");
        var timeout = Math.Clamp(OptionalInt(args, "timeout_ms", context.DefaultTimeoutMs), 1_000, context.MaxTimeoutMs);
        var maxChars = Math.Clamp(OptionalInt(args, "max_chars", context.MaxChars), 200, context.MaxChars);

        using var scope = EnterDirectory(ResolveWorkingDirectory(args, context));
        session.EnsureReferencesLoaded();

        var outcome = await session
            .EvaluateAsync(code, null, TimeSpan.FromMilliseconds(timeout), cancellationToken)
            .ConfigureAwait(false);
        var formatted = FormatEval(outcome, maxChars);

        if (saveAs is null || outcome.IsError) return formatted;

        // The object stays in the session, so get_result can print it again without re-running the code.
        session.SaveHandle(saveAs, outcome.Value, outcome.ValueType);
        var note = outcome.Value is null
            ? "saved '" + saveAs + "' (no value: the snippet returned nothing)"
            : "saved as '" + saveAs + "' - read it later with get_result, list_results shows all handles";
        return new ToolOutcome(formatted.Text + (char)10 + note, formatted.IsError);
    }

    private static async Task<ToolOutcome> GetResultAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var name = RequireString(args, "name");
        var maxChars = Math.Clamp(OptionalInt(args, "max_chars", context.MaxChars), 200, context.MaxChars);
        var drop = OptionalBool(args, "drop");

        if (!session.TryGetHandle(name, out var handle))
        {
            var known = string.Join(", ", session.Handles.Select(h => h.Name));
            return Fail(
                "No stored result named '" + name + "'." +
                (known.Length == 0
                    ? " Nothing is stored yet: run_script with save_as: \"name\"."
                    : " Known names: " + known + "."));
        }

        var rendered = await session.RenderValueAsync(handle.Value, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(name).Append(": ").Append(handle.TypeName ?? "null").Append((char)10);
        sb.Append(rendered).Append((char)10);
        sb.Append("saved ").Append(handle.SavedAt.ToString("HH:mm:ss")).Append((char)10);
        if (drop)
        {
            session.DropHandle(name);
            sb.Append("handle dropped.").Append((char)10);
        }

        return Truncate(sb.ToString().TrimEnd(), maxChars);
    }

    private static Task<ToolOutcome> ListResultsAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var handles = session.Handles;
        if (handles.Count == 0)
        {
            return Task.FromResult(new ToolOutcome(
                "No stored results. run_script with save_as: \"name\" keeps the returned object for get_result."));
        }

        var sb = new StringBuilder();
        foreach (var handle in handles)
        {
            sb.Append(handle.Name).Append(": ").Append(handle.TypeName ?? "null")
              .Append(" (saved ").Append(handle.SavedAt.ToString("HH:mm:ss")).Append(')').Append((char)10);
        }
        sb.Append(handles.Count).Append(" result handle(s); get_result <name> reads one.");
        return Task.FromResult(Truncate(sb.ToString(), context.MaxChars));
    }

    private static async Task<ToolOutcome> RunFileAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(args, context);
        var path = RequireString(args, "path");
        var full = ResolvePath(path, workingDirectory);
        if (!File.Exists(full))
        {
            return Fail("File not found: " + full);
        }

        var timeout = Math.Clamp(OptionalInt(args, "timeout_ms", context.DefaultTimeoutMs), 1_000, context.MaxTimeoutMs);
        var maxChars = Math.Clamp(OptionalInt(args, "max_chars", context.MaxChars), 200, context.MaxChars);
        var scriptArgs = OptionalStringArray(args, "args").ToArray();

        var code = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;

        // Script semantics resolve relative #r / file IO against the current directory, which the
        // engine captured when the session was built, so a per-run switch is the closest fit.
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = folder;
            session.EnsureReferencesLoaded();
            var outcome = await session
                .EvaluateAsync(code, scriptArgs, TimeSpan.FromMilliseconds(timeout), cancellationToken)
                .ConfigureAwait(false);

            var text = FormatEval(outcome, maxChars);
            var header = "file: " + full + "\n";
            return new ToolOutcome(header + text.Text, text.IsError);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static async Task<ToolOutcome> ResetSessionAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        // A reset drops the compile context, so every #r must be replayed or the session's
        // default usings stop resolving. Capture the list before it is cleared.
        var previous = session.References.ToArray();
        session.Reset();

        var (added, failed) = await AddAssemblyFilesAsync(previous, session, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("Session reset: variables, defined types and usings are gone; a fresh Roslyn session is built on the next call.").Append((char)10);
        sb.Append("re-applied ").Append(added.Count).Append(" of ").Append(previous.Length).Append(" reference(s).").Append((char)10);
        foreach (var failure in failed) sb.Append("  - ").Append(failure).Append((char)10);
        return Truncate(sb.ToString(), context.MaxChars);
    }

    private static async Task<ToolOutcome> HistoryAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(OptionalInt(args, "limit", 10), 1, 100);
        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        if (history.Count == 0) return new ToolOutcome("No submissions yet.");

        var start = Math.Max(0, history.Count - limit);
        var sb = new StringBuilder();
        for (var i = start; i < history.Count; i++)
        {
            sb.Append('#').Append(i + 1).Append('\n');
            sb.Append(history[i]).Append('\n');
        }
        return Truncate(sb.ToString(), context.MaxChars);
    }

    private static async Task<ToolOutcome> SessionStateAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var variables = await session.GetVariablesAsync(cancellationToken).ConfigureAwait(false);
        if (variables.Count == 0)
        {
            return new ToolOutcome("No variables in scope yet.");
        }

        var sb = new StringBuilder();
        foreach (var variable in variables.OrderBy(v => v.Name, StringComparer.Ordinal))
        {
            sb.Append(variable.Name).Append(": ").Append(variable.Type);
            if (variable.Value is not null) sb.Append(" = ").Append(variable.Value);
            sb.Append((char)10);
        }
        sb.Append(variables.Count).Append(" variable(s).");
        return Truncate(sb.ToString(), context.MaxChars);
    }

    internal static ToolOutcome FormatEval(EvalOutcome outcome, int maxChars)
    {
        var sb = new StringBuilder();
        if (outcome.IsError)
        {
            sb.Append("error: ").Append(outcome.Error ?? "unknown failure").Append('\n');
            if (!string.IsNullOrWhiteSpace(outcome.Diagnostics))
            {
                sb.Append("diagnostics:\n").Append(outcome.Diagnostics).Append('\n');
            }
        }
        else if (outcome.ValueText is not null)
        {
            sb.Append(outcome.ValueText).Append('\n');
            if (outcome.ValueType is not null) sb.Append('(').Append(outcome.ValueType).Append(')').Append('\n');
        }
        else
        {
            sb.Append("ok - statement executed, no value returned. ")
              .Append("End the snippet with an expression that has no trailing ';', or use 'return expr;'.\n");
        }

        var stdout = outcome.Stdout?.TrimEnd();
        if (!string.IsNullOrEmpty(stdout))
        {
            sb.Append("--- stdout ---\n").Append(stdout).Append('\n');
        }

        if (outcome.ElapsedMs >= 1_500)
        {
            sb.Append('[').Append(outcome.ElapsedMs).Append(" ms]\n");
        }

        // Truncate drops the flag, and a failed evaluation must reach the client as isError,
        // otherwise an agent reads a compilation error as a successful call.
        return new ToolOutcome(Truncate(sb.ToString().TrimEnd(), maxChars).Text, outcome.IsError);
    }
}
