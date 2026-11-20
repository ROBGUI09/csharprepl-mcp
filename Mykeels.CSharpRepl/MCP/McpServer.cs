using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mykeels.CSharpRepl.Mcp;

namespace Mykeels.CSharpRepl;

public sealed class McpServerOptions
{
    public string Name { get; init; } = "csharprepl";
    public string Version { get; init; } = "1.0.0";
    public string? Instructions { get; init; }
    public int DefaultTimeoutMs { get; init; } = 30_000;
    public int MaxTimeoutMs { get; init; } = 600_000;
    public int MaxChars { get; init; } = 20_000;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> PreloadPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();
    public bool WarmUp { get; init; } = true;
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Minimal, dependency-free MCP server over stdio (newline-delimited JSON-RPC 2.0).
/// Tool calls are executed sequentially: script state is inherently serialized, and running
/// two submissions concurrently would interleave a single Roslyn submission chain.
/// </summary>
public static class McpServer
{
    private const string LatestProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(
        McpServerOptions options,
        CSharpSession session,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        var context = new ToolContext
        {
            DefaultTimeoutMs = options.DefaultTimeoutMs,
            MaxTimeoutMs = options.MaxTimeoutMs,
            MaxChars = options.MaxChars,
            WorkingDirectory = options.WorkingDirectory ?? System.Environment.CurrentDirectory,
        };

        var log = options.Log ?? (_ => { });
        var stdin = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
        var stdout = new StreamWriter(output, new UTF8Encoding(false), 65536) { AutoFlush = true, NewLine = "\n" };

        void Send(JsonObject message)
        {
            try
            {
                stdout.Write(message.ToJsonString(SerializerOptions));
                stdout.Write('\n');
                stdout.Flush();
            }
            catch (Exception ex)
            {
                log("write failed: " + ex.Message);
            }
        }

        void SendResult(JsonNode? id, JsonNode result) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

        void SendError(JsonNode? id, int code, string message) =>
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        await PreloadAsync(options, session, context, log, cancellationToken).ConfigureAwait(false);
        log("ready");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                SendError(null, -32700, "Parse error: " + ex.Message);
                continue;
            }

            if (parsed is not JsonObject request) continue;

            var id = request["id"];
            var method = request["method"]?.GetValue<string>();
            var parameters = request["params"] as JsonObject;
            if (method is null) continue;

            switch (method)
            {
                case "initialize":
                {
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    var negotiated = requested is not null && SupportedProtocolVersions.Contains(requested)
                        ? requested
                        : LatestProtocolVersion;

                    var result = new JsonObject
                    {
                        ["protocolVersion"] = negotiated,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false },
                            ["logging"] = new JsonObject(),
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = options.Name,
                            ["version"] = options.Version,
                        },
                    };
                    if (!string.IsNullOrEmpty(options.Instructions))
                    {
                        result["instructions"] = options.Instructions;
                    }
                    SendResult(id, result);
                    break;
                }

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    break;

                case "ping":
                    SendResult(id, new JsonObject());
                    break;

                case "logging/setLevel":
                    SendResult(id, new JsonObject());
                    break;

                case "resources/list":
                    SendResult(id, new JsonObject { ["resources"] = new JsonArray() });
                    break;

                case "prompts/list":
                    SendResult(id, new JsonObject { ["prompts"] = new JsonArray() });
                    break;

                case "tools/list":
                {
                    var tools = new JsonArray();
                    foreach (var tool in McpTools.All)
                    {
                        tools.Add(new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["title"] = tool.Title,
                            ["description"] = tool.Description,
                            ["inputSchema"] = JsonNode.Parse(tool.InputSchema),
                        });
                    }
                    SendResult(id, new JsonObject { ["tools"] = tools });
                    break;
                }

                case "tools/call":
                {
                    var toolName = parameters?["name"]?.GetValue<string>();
                    var tool = toolName is null ? null : McpTools.Find(toolName);
                    if (tool is null)
                    {
                        SendError(id, -32602, "Unknown tool: " + toolName);
                        break;
                    }

                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                    ToolOutcome outcome;
                    try
                    {
                        outcome = await tool.Handler(arguments, session, context, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArgumentException ex)
                    {
                        outcome = new ToolOutcome("ERROR: " + ex.Message, true);
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = new ToolOutcome("ERROR: cancelled", true);
                    }
                    catch (Exception ex)
                    {
                        outcome = new ToolOutcome("ERROR: " + ex.GetType().Name + ": " + ex.Message, true);
                    }

                    var content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = outcome.Text },
                    };
                    var result = new JsonObject { ["content"] = content };
                    if (outcome.IsError) result["isError"] = true;
                    SendResult(id, result);
                    break;
                }

                default:
                    if (id is not null) SendError(id, -32601, "Method not found: " + method);
                    break;
            }
        }

        log("stdin closed");
        return 0;
    }

    private static async Task PreloadAsync(
        McpServerOptions options,
        CSharpSession session,
        ToolContext context,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (options.WarmUp)
        {
            try
            {
                await session.WarmUpAsync(cancellationToken).ConfigureAwait(false);
                log("roslyn warmed up");
            }
            catch (Exception ex)
            {
                log("warm-up failed: " + ex.Message);
            }
        }

        if (options.PreloadPaths.Count > 0)
        {
            var handler = McpTools.Find("add_reference");
            if (handler is not null)
            {
                var args = new JsonObject
                {
                    ["paths"] = new JsonArray(options.PreloadPaths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                };
                var outcome = await handler.Handler(args, session, context, cancellationToken).ConfigureAwait(false);
                log("preload: " + FirstLine(outcome.Text));
            }
        }

        foreach (var package in options.Packages)
        {
            var handler = McpTools.Find("add_package");
            if (handler is null) continue;

            var parts = package.Split('@', 2);
            var args = new JsonObject { ["id"] = parts[0] };
            if (parts.Length == 2) args["version"] = parts[1];
            var outcome = await handler.Handler(args, session, context, cancellationToken).ConfigureAwait(false);
            log("package " + package + ": " + FirstLine(outcome.Text));
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        var line = index < 0 ? text : text.Substring(0, index);
        return line.Length <= 200 ? line : line.Substring(0, 200);
    }

    private const string DefaultInstructions =
        "C# scripting backed by Roslyn. One long-lived session: variables, types, usings, references and loaded " +
        "assemblies persist across calls, so build work up incrementally instead of sending one giant snippet. " +
        "Use add_reference to preload local DLLs, inspect_assembly before loading an untrusted one (loading runs " +
        "module initializers), describe_type / list_types to explore what is available, and decompile to read an " +
        "implementation. Console output of a snippet is captured and returned in the result, not streamed. " +
        "The server is not bound to one directory: run_script, run_file, add_reference and add_project take an " +
        "optional cwd for that call only (absolute, or relative to the server default; %VAR% is expanded), so " +
        "pass the project folder you need instead of assuming the process directory.";

    /// <summary>Stdio entry point used by the sample app and by MCP client configurations.</summary>
    public static async Task<int> RunStandalone(string[] args, Type? globalsType = null, string? serverName = null)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.Error.WriteLine(
                "Usage: mcp [--cwd DIR] [--preload PATH|DIR]... [--package ID[@VERSION]]... [--using NAMESPACE]... "
                    + "[--timeout MS] [--max-chars N] [--no-warmup]"
            );
            Console.Error.WriteLine("Every call may also pass its own \"cwd\" argument; --cwd only sets the default.");
            return 0;
        }

        var options = Parse(args, globalsType, serverName, out var extraUsings);
        var compileReferences = new List<string>();
        if (globalsType is not null && !string.IsNullOrEmpty(globalsType.Assembly.Location))
        {
            compileReferences.Add(globalsType.Assembly.Location);
        }

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var capture = new CaptureTextWriter();

        // The engine prints to Console.Out, which for a stdio server is the protocol channel.
        // Everything the engine writes is captured instead and returned inside tool results.
        var originalOut = Console.Out;
        Console.SetOut(capture);
        try
        {
            var session = new CSharpSession(capture, extraUsings, compileReferences);
            return await RunAsync(options, session, stdin, stdout, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(originalOut);
            stdout.Flush();
        }
    }

    /// <summary>Backward-compatible entry point: starts a stdio MCP server for the given globals type.</summary>
    public static Task<int> Run(Type globalsType, string? serverName = null) =>
        RunStandalone(Array.Empty<string>(), globalsType, serverName);

    private static McpServerOptions Parse(string[] args, Type? globalsType, string? serverName, out string[] usings)
    {
        var preload = new List<string>();
        var packages = new List<string>();
        var extraUsings = new List<string>();
        var defaultTimeout = 30_000;
        var maxChars = 20_000;
        var warmUp = true;
        string? workingDirectory = null;

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "--cwd":
                {
                    // Optional: per-call "cwd" arguments override this, and without it the process
                    // directory is used, so the server is not tied to one project.
                    var value = Next();
                    if (value is not null)
                    {
                        var full = Path.GetFullPath(value);
                        if (Directory.Exists(full))
                        {
                            workingDirectory = full;
                            Directory.SetCurrentDirectory(full);
                        }
                    }
                    break;
                }
                case "--preload":
                case "--add":
                {
                    var value = Next();
                    if (value is not null) preload.Add(value);
                    break;
                }
                case "--package":
                {
                    var value = Next();
                    if (value is not null) packages.Add(value);
                    break;
                }
                case "--using":
                {
                    var value = Next();
                    if (value is not null) extraUsings.Add(value);
                    break;
                }
                case "--timeout":
                {
                    if (int.TryParse(Next(), out var ms)) defaultTimeout = Math.Clamp(ms, 1000, 600_000);
                    break;
                }
                case "--max-chars":
                {
                    if (int.TryParse(Next(), out var chars)) maxChars = Math.Clamp(chars, 1000, 400_000);
                    break;
                }
                case "--no-warmup":
                    warmUp = false;
                    break;
            }
        }

        // A single-file publish bundles the host assembly, so Location is empty and there is no
        // path to reference; in that case the globals namespace must not be injected either.
        var globalsLocation = globalsType?.Assembly.Location;
        if (globalsType?.Namespace is { Length: > 0 } ns && !string.IsNullOrEmpty(globalsLocation))
        {
            extraUsings.Add(ns);
        }

        // The globals type lives in the host application's assembly, which the default reference set
        // does not include; preloading it keeps the injected namespace using resolvable.
        if (globalsType is not null && !string.IsNullOrEmpty(globalsType.Assembly.Location))
        {
            preload.Insert(0, globalsType.Assembly.Location);
        }

        usings = extraUsings.ToArray();
        return new McpServerOptions
        {
            Name = serverName ?? "csharprepl",
            Version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            Instructions = DefaultInstructions,
            DefaultTimeoutMs = defaultTimeout,
            MaxChars = maxChars,
            WorkingDirectory = workingDirectory,
            PreloadPaths = preload,
            Packages = packages,
            WarmUp = warmUp,
        };
    }
}
