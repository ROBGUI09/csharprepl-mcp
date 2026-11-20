// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{
    /// <summary>
    /// Counts from a test runner summary. Two runners are supported: VSTest prints
    /// "Failed: 1, Passed: 3, Skipped: 0, Total: 4" and Microsoft.Testing.Platform prints
    /// "failed: 1, passed: 3, skipped: 0, total: 4". Both are matched in any language.
    /// </summary>
    private sealed record TestCounts(int Passed, int Failed, int Skipped, int Total)
    {
        public bool Any => Passed + Failed + Skipped + Total > 0;
    }

    private static readonly Regex VstestSummary = new(
        @"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Four consecutive "label: number" pairs in any language. Microsoft.Testing.Platform is localized,
    /// so its summary line cannot be matched by label text - only by the fixed order of the counts.
    /// </summary>
    private static readonly Regex LabeledCounts = new(
        @"[\p{L}][\p{L}\p{M} ]{1,24}:\s*(?<n1>\d+)[,;]?\s*" +
        @"[\p{L}][\p{L}\p{M} ]{1,24}:\s*(?<n2>\d+)[,;]?\s*" +
        @"[\p{L}][\p{L}\p{M} ]{1,24}:\s*(?<n3>\d+)[,;]?\s*" +
        @"[\p{L}][\p{L}\p{M} ]{1,24}:\s*(?<n4>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex EnglishSummary = new(
        @"(?<passed>\d+)\s+passed,\s+(?<failed>\d+)\s+failed,\s+(?<skipped>\d+)\s+skipped,\s+(?<total>\d+)\s+total",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static async Task<ToolOutcome> RunTestsAsync(
        JsonObject args, CSharpSession session, ToolContext context, CancellationToken cancellationToken)
    {
        var path = RequireString(args, "path");
        var filter = OptionalString(args, "filter");
        var configuration = OptionalString(args, "configuration") ?? "Debug";
        var timeout = Math.Clamp(OptionalInt(args, "timeout_ms", 300_000), 5_000, context.MaxTimeoutMs);
        var maxChars = Math.Clamp(OptionalInt(args, "max_chars", context.MaxChars), 500, context.MaxChars);
        var workingDirectory = ResolveWorkingDirectory(args, context);
        var full = ResolvePath(path, workingDirectory);

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return Fail("Test target not found: " + full);
        }

        var command = new StringBuilder("test \"").Append(full).Append("\" -c ").Append(configuration)
            .Append(" --nologo");
        if (filter is not null)
        {
            command.Append(" --filter \"").Append(filter).Append('"');
        }

        var startedAt = DateTimeOffset.Now;
        var (exitCode, output) = await RunProcessAsync(
                "dotnet",
                command.ToString(),
                Directory.Exists(full) ? full : Path.GetDirectoryName(full)!,
                TimeSpan.FromMilliseconds(timeout),
                cancellationToken)
            .ConfigureAwait(false);

        var text = output.Replace("\r", string.Empty);
        var counts = ParseCounts(text);
        var failures = ParseFailedTests(text);
        var elapsed = (long)(DateTimeOffset.Now - startedAt).TotalSeconds;

        string status;
        bool isError;
        if (counts.Any)
        {
            status = counts.Failed == 0 ? "passed" : "failed";
            isError = false;
        }
        else if (isEmptyRun(text))
        {
            status = "no tests";
            isError = true;
        }
        else if (text.Contains("testhost", StringComparison.OrdinalIgnoreCase) &&
                 text.Contains("Win32Exception", StringComparison.Ordinal))
        {
            status = "test host could not start";
            isError = true;
        }
        else
        {
            status = exitCode == 0 ? "no summary" : "build or run failed";
            isError = true;
        }

        if (environmentBlocked(text))
        {
            status += " (blocked by this host)";
            isError = true;
        }

        var sb = new StringBuilder();
        sb.Append("status: ").Append(status).Append((char)10);
        sb.Append("exit: ").Append(exitCode).Append(" after ").Append(elapsed).Append("s").Append((char)10);

        if (counts.Any)
        {
            sb.Append("counts: ")
              .Append(counts.Passed).Append(" passed, ")
              .Append(counts.Failed).Append(" failed, ")
              .Append(counts.Skipped).Append(" skipped, ")
              .Append(counts.Total).Append(" total").Append((char)10);
        }

        if (failures.Count > 0)
        {
            sb.Append("failed tests (").Append(failures.Count).Append("):").Append((char)10);
            foreach (var test in failures.Take(50)) sb.Append("  - ").Append(test).Append((char)10);
        }
        else if (counts.Failed > 0)
        {
            sb.Append("failed tests: ").Append(counts.Failed)
              .Append(" — this runner does not print their names to stdout; the tail below or its run log has them")
              .Append((char)10);
        }

        if (environmentBlocked(text))
        {
            sb.Append("note: the runner process was denied process access (Win32Exception 5).\n");
            sb.Append("  Microsoft.Testing.Platform-based runners (xunit.v3, MSTest 3.6+, TUnit) are not\n");
            sb.Append("  affected; classic VSTest needs a host that allows opening child processes.\n");
        }

        var tailLines = counts.Any ? 12 : 60;
        sb.Append("output:").Append((char)10).Append(Tail(text, tailLines));

        return new ToolOutcome(Truncate(sb.ToString().TrimEnd(), maxChars).Text, isError);
    }

    private static bool environmentBlocked(string text) =>
        text.Contains("Win32Exception (5)", StringComparison.Ordinal)
        || text.Contains("Отказано в доступе.", StringComparison.Ordinal);

    private static bool isEmptyRun(string text) =>
        text.Contains("No test source files", StringComparison.OrdinalIgnoreCase)
        || text.Contains("No test is available", StringComparison.OrdinalIgnoreCase)
        || text.Contains("is not a test project", StringComparison.OrdinalIgnoreCase)
        || text.Contains("No tests found", StringComparison.OrdinalIgnoreCase);

    private static TestCounts ParseCounts(string text)
    {
        var match = VstestSummary.Match(text);
        if (match.Success) return From(match, "failed", "passed", "skipped", "total");

        match = EnglishSummary.Match(text);
        if (match.Success) return From(match, "failed", "passed", "skipped", "total");

        // Microsoft.Testing.Platform prints localized labels in a fixed order:
        // failed, passed, skipped, total - then an optional duration that must be ignored.
        // A translation could reorder them, so a candidate counts only when the numbers add up;
        // the fallback keeps the best effort instead of reporting nothing.
        var labeled = LabeledCounts.Matches(text);
        if (labeled.Count > 0)
        {
            for (var index = labeled.Count - 1; index >= 0; index--)
            {
                var candidate = labeled[index];
                var failed = Int(candidate, "n1");
                var passed = Int(candidate, "n2");
                var skipped = Int(candidate, "n3");
                var total = Int(candidate, "n4");
                if (failed + passed + skipped == total) return new TestCounts(passed, failed, skipped, total);
            }

            var last = labeled[labeled.Count - 1];
            return new TestCounts(Int(last, "n2"), Int(last, "n1"), Int(last, "n3"), Int(last, "n4"));
        }

        return new TestCounts(0, 0, 0, 0);
    }

    private static TestCounts From(Match match, string failed, string passed, string skipped, string total) =>
        new(
            Int(match, passed),
            Int(match, failed),
            Int(match, skipped),
            Int(match, total));

    private static int Int(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static IReadOnlyList<string> ParseFailedTests(string text)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = trimmed.Substring("Failed ".Length).Trim();
            if (rest.StartsWith("!", StringComparison.Ordinal)) continue;
            var name = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (name is null || name.Contains(':', StringComparison.Ordinal)) continue;
            found.Add(name.Trim('[', ']'));
        }

        return found.ToArray();
    }

    private static string Tail(string text, int lineCount)
    {
        var lines = text.TrimEnd().Split('\n');
        if (lines.Length <= lineCount) return string.Join("\n", lines);
        return "… " + (lines.Length - lineCount) + " earlier line(s)" + (char)10 +
               string.Join("\n", lines.Skip(lines.Length - lineCount));
    }
}