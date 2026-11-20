// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mykeels.CSharpRepl.Mcp;

public static partial class McpTools
{
    private static async Task<ToolOutcome> AddProjectAsync(
        JsonObject args,
        CSharpSession session,
        ToolContext context,
        CancellationToken cancellationToken
    )
    {
        var path = RequireString(args, "path");
        var configuration = OptionalString(args, "configuration") ?? "Debug";
        var build = OptionalBool(args, "build", true);
        var workingDirectory = ResolveWorkingDirectory(args, context);
        var projectPath = ResolveProjectPath(path, workingDirectory);
        if (projectPath is null)
        {
            return Fail($"No .csproj found at '{path}' (pass the .csproj itself or a directory containing one).");
        }

        var sb = new StringBuilder();
        sb.Append("project: ").Append(projectPath).Append((char)10);

        if (build)
        {
            var (exitCode, output) = await RunProcessAsync(
                    "dotnet",
                    $"build \"{projectPath}\" -c {configuration} -v:q -nologo",
                    Path.GetDirectoryName(projectPath)!,
                    TimeSpan.FromSeconds(300),
                    cancellationToken
                )
                .ConfigureAwait(false);

            sb.Append("build: ")
                .Append(exitCode == 0 ? "ok" : $"FAILED (exit {exitCode})")
                .Append((char)10);
            if (exitCode != 0)
            {
                sb.Append(Truncate(output, context.MaxChars / 2).Text).Append((char)10);
                return new ToolOutcome(sb.ToString(), IsError: true);
            }
        }

        var target = await GetTargetPathAsync(projectPath, configuration, cancellationToken).ConfigureAwait(false);
        if (target is null || !File.Exists(target))
        {
            return Fail(
                $"Built, but could not locate the output assembly for '{projectPath}'. "
                    + "Pass build=false and add_reference the DLL directly if the project uses a custom output path."
            );
        }

        var (added, failed) = await AddAssemblyFilesAsync(new[] { target }, session, cancellationToken)
            .ConfigureAwait(false);
        sb.Append("assembly: ").Append(target).Append((char)10);
        sb.Append("referenced: ").Append(added.Count > 0 ? "yes" : "no").Append((char)10);
        if (added.Count == 0)
        {
            sb.Append("details: ").Append(string.Join("; ", failed)).Append((char)10);
        }

        sb.Append(
            "Types from this assembly are available to run_script now; use list_types/describe_type to explore them."
        );
        return new ToolOutcome(Truncate(sb.ToString(), context.MaxChars).Text, added.Count == 0);
    }

    private static string? ResolveProjectPath(string path, string workingDirectory)
    {
        var full = ResolvePath(path, workingDirectory);
        if (File.Exists(full))
        {
            return full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        var projects = Directory.GetFiles(full, "*.csproj", SearchOption.TopDirectoryOnly);
        return projects.Length == 1 ? projects[0] : null;
    }

    private static async Task<string?> GetTargetPathAsync(
        string projectPath,
        string configuration,
        CancellationToken cancellationToken
    )
    {
        var (_, output) = await RunProcessAsync(
                "dotnet",
                $"msbuild \"{projectPath}\" -getProperty:TargetPath -p:Configuration={configuration} -nologo",
                Path.GetDirectoryName(projectPath)!,
                TimeSpan.FromSeconds(120),
                cancellationToken
            )
            .ConfigureAwait(false);

        var candidate = output
            .Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (candidate is not null && File.Exists(candidate))
        {
            return candidate;
        }

        // Older SDKs do not support -getProperty; fall back to the conventional layout.
        var directory = Path.GetDirectoryName(projectPath)!;
        var name = Path.GetFileNameWithoutExtension(projectPath);
        if (!Directory.Exists(Path.Combine(directory, "bin")))
        {
            return null;
        }

        return Directory
            .GetFiles(
                Path.Combine(directory, "bin", configuration),
                name + ".dll",
                SearchOption.AllDirectories
            )
            .FirstOrDefault();
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The CLI writes UTF-8 even when redirected; the default would be the OEM code page
            // and would turn every localised message into mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // MSBuild and restore write to temp/cache locations that a restricted host may forbid.
        McpCache.ConfigureChildEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            output.AppendLine(
                timeoutSource.IsCancellationRequested
                    ? $"timed out after {timeout.TotalSeconds:F0}s"
                    : "cancelled"
            );
            return (-1, output.ToString());
        }

        return (process.ExitCode, output.ToString());
    }
}
