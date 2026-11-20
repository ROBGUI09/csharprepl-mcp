# Mykeels.CSharpRepl

This library is a stripped-down, plug-n-play version of [CSharpRepl.Services](https://github.com/waf/CSharpRepl/tree/bd79130d49c06736a2d5f4d56ac7643889ad2328/CSharpRepl.Services). It is a powerful C# REPL (Read-Eval-Print Loop) that can be embedded into any .NET application, providing an interactive C# REPL environment with syntax highlighting, code completion, MCP server, and more.

## Upstream and scope of this fork

This repository is a fork of [mykeels/Mykeels.CSharpRepl](https://github.com/mykeels/Mykeels.CSharpRepl),
which extracts the REPL services from [waf/CSharpRepl](https://github.com/waf/CSharpRepl). Sources keep their
upstream headers and are under the Mozilla Public License 2.0 — see [LICENSE](LICENSE).

What this fork adds on top of upstream: the stdio MCP server (`Mykeels.CSharpRepl/MCP/`) with its 19 tools, the
per-call `cwd` model behind it, the self-contained publish recipe, and `mcp-smoke.py`. The new files carry the
same license. This is an independent fork and is not affiliated with the upstream authors.

Read [SECURITY.md](SECURITY.md) before pointing this at anything you care about: the server executes the C# it
is given.

## Installation

Copy the [nuget.config](nuget.config) file to your project directory. This is necessary to ensure that the Microsoft.SymbolStore package can be found.

```bash
dotnet add package Mykeels.CSharpRepl
```

## Quick Start

Here's a minimal example of how to use Mykeels.CSharpRepl in your application to launch a REPL:

```csharp
using Mykeels.CSharpRepl;

await Repl.Run();
```

This will start an interactive C# REPL with default settings.

## Configuration

You can customize the REPL by providing a `Configuration` object:

```csharp
using CSharpRepl.Services;
using Mykeels.CSharpRepl;
using Spectre.Console;

await Repl.Run(
    new Configuration(
        // Add references to assemblies
        references: AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => $"{a.GetName().Name}.dll")
            .ToArray(),
        
        // Add default namespaces
        usings: [
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks"
        ],
        
        // Set application name
        applicationName: "MyApp.CSharpRepl",
        
        // Customize success output
        logSuccess: (message, result) => {
            Console.WriteLine($"<< {message}");
            string output = Newtonsoft.Json.JsonConvert.SerializeObject(result);
            AnsiConsole.MarkupLine($"[green]>> {output}[/]");
        },
        
        // Customize error output
        logError: (message, exception, _) => {
            Console.WriteLine($"<< {message}");
            string output = Newtonsoft.Json.JsonConvert.SerializeObject(
                new { Error = exception }
            );
            Console.WriteLine($">> {output}");
            AnsiConsole.MarkupLine($"[red]>> {output.EscapeMarkup()}[/]");
        }
    )
);
```

## Pre-execution Commands

You can specify commands to be executed before the REPL starts. This is useful for setting up the environment or importing commonly used types:

```csharp
await Repl.Run(
    commands: [
        // Import ScriptGlobals to make its methods available directly
        "using static Mykeels.CSharpRepl.Sample.ScriptGlobals;"
    ]
);
```

## ScriptGlobals

You can add your own ScriptGlobals by adding a static class with static methods and properties, and then running a pre-execution command on REPL startup.

```csharp
"using static Mykeels.CSharpRepl.Sample.ScriptGlobals;"
```

Any class named `*ScriptGlobals` that's brought into scope this way — at startup via `commands`, or by the user typing a `using static` statement mid-session — is picked up automatically by the `help` command, which lists its public static members alongside their type information (parameter types, return types, property types). No extra configuration needed; this works the same whether you're in the terminal REPL or a Slack session.

## Exploring Types with `?`

Beyond the automatic `*ScriptGlobals` listing, you can introspect anything on demand by typing `<expr> ?` — the member list (with type information) prints the same way `help` prints one for `*ScriptGlobals`:

```csharp
DateTime ?      // an unqualified type name, resolved via an active `using`
System.DateTime ?   // a fully-qualified type name
Http ?          // an in-scope expression — evaluated, then its runtime type is introspected
```

- If `<expr>` resolves to a type name (fully-qualified, or unqualified via a namespace already brought into scope with `using`), that type's members are listed directly.
- Otherwise, `<expr>` is evaluated as C# and the *runtime type of the result* is introspected instead — this is what makes `Http ?` useful: `Http` is a property (e.g. from `ScriptGlobals`), so it's evaluated and whatever it returns (an `HttpClient`, say) gets introspected.
- If neither works, you'll get a "Couldn't resolve" message instead of a compile error.

This works the same in the terminal REPL and in a Slack session, and is the quickest way to explore what's available without leaving the REPL — e.g. `Http ?` to see what methods an injected client offers, or `SomeResult ?` to check what a previous expression's return value looks like.

## Browsing Result History

The terminal REPL keeps the last 64 commands and their results in memory, and you can scroll back through them in a full-screen JSON view. Press **Ctrl+O** at the prompt to open it at the most recent result:

| Key | Action |
| --- | --- |
| `←` / `→` | Go to an older / newer result |
| `↑` / `↓` | Scroll the JSON one line |
| `Space` / `B` (or `PgDn` / `PgUp`) | Scroll the JSON one screen down / up |
| `Home` / `End` | Jump to the top / bottom of the JSON |
| `Esc` / `Enter` / `Q` / `Ctrl+O` | Close the viewer and return to the prompt |

The panel header shows which result you're on (e.g. `Result 3/5`) and which lines are visible. Results have a green border and errors a red one. Keys and values are colour-coded by JSON type, and the command that produced the result is shown above the panel.

Notes:

- The history lasts only for the session; it isn't saved to disk.
- Only the interactive terminal REPL records history. Slack sessions and the MCP server don't.
- `Ctrl+↑` / `Ctrl+↓` also open the viewer, and move between results inside it, but only where the OS lets them through. On macOS they're taken by Mission Control and App Exposé by default, which is why `Ctrl+O` and `←` / `→` are the main keys.

## MCP Server

`McpServer` exposes the REPL as a [Model Context Protocol](https://modelcontextprotocol.io) server over stdio. It keeps **one long-lived Roslyn session**, so variables, defined types, references and usings survive between tool calls — a model can build something up over several calls instead of re-declaring everything each time.

### Tools

| Tool | What it does |
|---|---|
| `run_script` | Evaluate a C# snippet in the persistent session; `save_as` keeps the returned object for `get_result`. |
| `run_file` | Evaluate a `.csx` / `.cs` file; relative paths resolve against `cwd`, and the process switches to the file's folder for the run. |
| `add_reference` | `#r` one or more DLLs — files or directories, optionally recursive. |
| `add_package` | Download a NuGet package and reference its best-matching `lib` target framework. |
| `add_project` | `dotnet build` a `.csproj` and reference its output assembly. |
| `list_types` | Regex search over the types in the loaded assemblies. |
| `describe_type` | Constructors, methods, properties, fields and events of one type. |
| `inspect_assembly` | Metadata only: version, culture, entry point, references, type count. |
| `decompile` | ILSpy decompilation of an assembly, a type, or a single member. |
| `list_references` | The `#r` list plus the assemblies actually loaded into the process. |
| `session_state` | Variables currently in scope, with types and short value previews. |
| `reset_session` | Drop the script state and rebuild it, replaying all references. |
| `history` | The submissions made in this session. |
| `get_result` | Print an object kept by `save_as` — the live reference, not a copy, so it shows later mutations. |
| `list_results` | Names and types of the stored handles. |
| `analyze_file` | Triage a binary without loading it: identity, entry points, non-framework dependencies, namespace sizes, and the metadata strings (URLs, config keys, SQL, file names) with library noise filtered out. |
| `diff_assemblies` | Compare the public API of two builds: added/removed types and members, and a `compatible` / `breaking` verdict. |
| `run_tests` | `dotnet test` a project, solution or folder and summarize: counts, failing test names, output tail. |
| `attach_process` | Modules a running process has mapped, resolved to assembly identity. Read-only. |

### Launching

```csharp
await McpServer.RunStandalone(args);                        // no globals type
await McpServer.RunStandalone(args, typeof(ScriptGlobals));  // ScriptGlobals members usable from scripts
```

`McpServer.Run(args, globalsType)` remains as an alias for `RunStandalone`.

The server is not tied to one project. It starts in whatever directory the client launched it from, and
every tool that touches the file system (`run_script`, `run_file`, `add_reference`, `add_project`) accepts
a **`cwd` argument for that call only**; the process directory is restored afterwards, and `%VAR%` is
expanded. `--cwd` merely changes the fallback default, so a single server instance can work across
several repositories without restarting.

Flags, all optional (forward them from `Program.Main`, e.g. `await McpServer.RunStandalone(args, typeof(ScriptGlobals));`):

| Flag | Meaning |
|---|---|
| `--cwd <path>` | Fallback working directory for calls that omit `cwd`. Optional. |
| `--preload <path>`, `--add <path>` | `#r` a DLL at startup; repeatable, relative paths allowed. |
| `--package <id>[@<version>]` | Fetch and reference a NuGet package at startup; repeatable. |
| `--using <namespace>` | Extra `using` available in every submission; repeatable. |
| `--timeout <ms>` | Default per-call timeout (clamped to 1000–600000). |
| `--max-chars <n>` | Truncation limit for tool output (200–20000). |
| `--no-warmup` | Skip the background warm-up; the first call gets slower. |

### Standalone executable

A self-contained `.exe` needs no installed .NET runtime:

```bash
dotnet publish Mykeels.CSharpRepl.Sample/Mykeels.CSharpRepl.Sample.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false -o dist/win-x64
```

`dist/win-x64/Mykeels.CSharpRepl.Sample.exe` is the server; MCP clients launch it directly, without a
`dotnet` prefix. Keep the folder intact — the engine resolves Roslyn and ILSpy assemblies relative to the
executable.

**Do not use `PublishSingleFile=true`.** Roslyn's workspace turns loaded assemblies into metadata
references through `Assembly.Location`, which is empty for bundled assemblies, so every `run_script`
fails with `NotSupportedException: Cannot create a metadata reference to an assembly without location`.
Publish to a folder instead. `add_project` still needs the .NET SDK on `PATH`, because it shells out to
`dotnet build`.

The exe keeps a `cache` folder beside the binary (`cache/tmp`, `cache/packages`, `cache/home`): NuGet
package downloads, MSBuild temp and the child `dotnet` CLI home all live there, and child processes
inherit them as `TMP`/`TEMP`. That is what lets builds run from a host whose `%TEMP%` is not writable —
a sandboxed agent runtime, for example. Override the location with `CSHARPREPL_MCP_CACHE=<path>`; if the
install folder is read-only the server falls back to `%LOCALAPPDATA%\csharprepl-mcp`, then
`%TEMP%\csharprepl-mcp`.

### Client configuration

Point any MCP client at the published binary:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "C:/path/to/Mykeels.CSharpRepl/dist/win-x64/Mykeels.CSharpRepl.Sample.exe",
      "args": ["--preload", "C:/path/to/MyLib/bin/Debug/net8.0/MyLib.dll"]
    }
  }
}
```

No `cwd` anywhere: each call passes the directory it needs, e.g.
`{"code": "File.ReadAllText(\"data.xml\")", "cwd": "C:/path/to/project/data"}`. Add `--cwd` only if
you want one project as the default.

The same server, launched through the shared runtime instead of the self-contained exe:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": ["C:/path/to/Mykeels.CSharpRepl/dist/mcp/Mykeels.CSharpRepl.Sample.dll"]
    }
  }
}
```

Or straight from source, without publishing:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": [
        "run", "--project", "C:/path/to/Mykeels.CSharpRepl/Mykeels.CSharpRepl.Sample",
        "--no-build", "--"
      ]
    }
  }
}
```

### Prebuilt binaries

Every tagged [release](https://github.com/ROBGUI09/csharprepl-mcp/releases) carries two archives:
`Mykeels.CSharpRepl-mcp-win-x64-v*.zip` (self-contained, no .NET runtime needed, Windows x64) and
`Mykeels.CSharpRepl-mcp-portable-v*.zip` (framework-dependent, needs .NET 8). Unpack and point the client at the
binary inside — `Mykeels.CSharpRepl.Sample.exe` or `dotnet Mykeels.CSharpRepl.Sample.dll`. CI builds both on every
push; pushing a `v*` tag publishes them.

### Verifying an installation

`mcp-smoke.py` drives a published server the way a real client does — newline-delimited JSON-RPC over
stdio — and exercises every tool end to end, including a `dotnet build`, a NuGet download and a session
reset:

```bash
python mcp-smoke.py dist/win-x64/Mykeels.CSharpRepl.Sample.exe   # or dist/mcp/Mykeels.CSharpRepl.Sample.dll
```

It prints one line per check and exits non-zero if any fails. `SMOKE_SKIP_NETWORK=1` and
`SMOKE_SKIP_BUILD=1` drop the slow checks; `SMOKE_VERBOSE=1` echoes every tool result. The suite builds
two throwaway xunit.v3 projects for `run_tests` — one green, one red — so it needs network access for the
first restore and the .NET SDK on `PATH`.

### Behaviour worth knowing

- Console output from a snippet (`Console.WriteLine`, third-party libraries writing to the console) never reaches the protocol stream. Everything is captured and returned inside the tool result, so a normal result can look like `42 (System.Int32)` followed by a `--- stdout ---` block.
- A snippet that ends in a statement gets a note instead of a value — end with an expression that has no trailing `;`, or use `return expr;`.
- A failed evaluation is a failed tool call: compilation errors, exceptions and timeouts come back as
  `isError: true` with the message and the diagnostics, so a client cannot mistake them for a result.
- References are loaded into the process before each evaluation, which is why a DLL rebuilt by
  `add_project` is usable in the very next call.
- `inspect_assembly`, `analyze_file`, `diff_assemblies` and `attach_process` read the PE/metadata without loading the DLL into the process: use them on binaries you do not trust. `add_reference` performs `Assembly.LoadFrom`, which runs module initializers — that is the same trust as running the code.
- Result handles (`save_as` → `get_result`) hold a real object in the live session. They survive between calls and vanish on `reset_session`.
- `run_tests` reports a failing test as a result (`status: failed`, `isError: false`); only a run that produced no results at all — build failure, missing SDK, timeout, no test project — is a tool error. It shells out to `dotnet test`, so it needs the SDK like `add_project`. Counts are read from the
  summary line whatever the UI language: English labels (`Failed: 1, Passed: 3, Skipped: 0, Total: 4`, printed
  by both runners) are matched exactly, other languages by the fixed failed → passed → skipped → total order,
  accepted only when the numbers add up. Runners on Microsoft.Testing.Platform (xunit.v3, MSTest 3.6+, TUnit) work anywhere; classic VSTest runners start a `testhost` child process and need a host that allows opening child processes — a sandbox that denies `OpenProcess` makes them fail with `Win32Exception (5)`, which the tool reports as `blocked by this host`.
- `attach_process` is a module snapshot, not a debugger: it attaches nothing, injects nothing, and cannot read another process's managed state. A 32-bit target can only be read from a 32-bit host, so access-denied usually means bitness or privilege.
- `--timeout` / `timeout_ms` cancel compilation and execution through a `CancellationToken`. Cancellation is not preemption: a call that timed out mid-flight may leave the script state half-applied, so `reset_session` and re-run.
- `add_package` resolves only the package's own `lib` assemblies. Its transitive dependencies are not downloaded — add them explicitly if types fail to load.
- A relative `cwd` resolves against the server default, which is the launch directory unless `--cwd`
  was given; a missing directory is a tool error, not a crash.
- Package downloads, MSBuild temp and the child `dotnet` home share one cache root (the `cache` folder
  next to the exe, `CSHARPREPL_MCP_CACHE`, or a fallback); deleting it is always safe.
- Non-ASCII source and output are UTF-8 end to end.

## Slack Integration

You can host the REPL over Slack instead of (or as well as) a terminal: a user runs a slash command in Slack, the bot starts a thread, and messages sent in that thread are evaluated as REPL input, with results/errors posted back as replies. Multiple threads can each run their own independent session.

### 1. Create the Slack app

The easiest way is from a manifest — go to [api.slack.com/apps](https://api.slack.com/apps) → **Create New App → From an app manifest**, and paste in:

```json
{
  "display_information": {
    "name": "Mykeels C# REPL",
    "description": "Run a C# REPL session in a Slack thread",
    "background_color": "#2c2d30"
  },
  "features": {
    "bot_user": {
      "display_name": "mykeels-csharp-repl",
      "always_online": true
    },
    "slash_commands": [
      {
        "command": "/mykeels-csharp-repl",
        "description": "Start a new C# REPL session in a thread",
        "usage_hint": "new",
        "should_escape": false
      }
    ]
  },
  "oauth_config": {
    "scopes": {
      "bot": ["chat:write", "commands", "channels:history", "groups:history", "im:history", "files:write"]
    }
  },
  "settings": {
    "event_subscriptions": {
      "bot_events": ["message.channels", "message.groups", "message.im"]
    },
    "interactivity": {
      "is_enabled": true
    },
    "org_deploy_enabled": false,
    "socket_mode_enabled": true,
    "token_rotation_enabled": false
  }
}
```

Notes on the manifest:

- `interactivity.is_enabled: true` is required even though this app has no buttons/modals — Slack routes slash-command payloads through the interactivity pipeline when Socket Mode is on, so it won't deliver them without this.
- Each `message.*` event needs its matching `*:history` scope to read message content in that conversation type: `message.channels` → `channels:history`, `message.groups` → `groups:history`, `message.im` → `im:history`. If you only need sessions in public channels, drop `message.groups`/`message.im` and their scopes.

A manifest can't do everything, though — after creating the app:

1. **Generate an app-level token.** Under **Basic Information → App-Level Tokens**, create one with the `connections:write` scope (starts with `xapp-`). This is what lets `SlackReplHost` use Socket Mode's WebSocket connection — the bot token alone isn't enough, and there's no way to bake this into the manifest since it's generated per-install.
2. **Install the app to your workspace.** Under **OAuth & Permissions**, install it to get a bot token (starts with `xoxb-`).
3. **Invite the bot** to whichever channels it should work in.

(If you'd rather configure it by hand instead of from a manifest: enable Socket Mode; add the bot token scopes above under OAuth & Permissions; create the slash command under Slash Commands — no Request URL needed with Socket Mode; enable Event Subscriptions and subscribe to the `message.*` events you need. Then do the three steps above.)

### 2. Run the host

```csharp
using Mykeels.CSharpRepl;

await SlackReplHost.Run(
    new SlackReplOptions
    {
        BotToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")!,
        AppToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN")!,
        SlashCommand = "/mykeels-csharp-repl", // must match what you registered above
        AllowedChannelIds = ["C0123456789"], // or AllowedUserIds — at least one is required, see below
    }
);
```

`SlackReplHost.Run` connects over Socket Mode and runs until cancelled (pass a `CancellationToken` to stop it), starting a new `Repl.Run` session — with its own `RoslynServices`, independent of any other open session — for every slash command invocation.

### 3. Authorization

At least one of `AllowedUserIds` or `AllowedChannelIds` must be set — `SlackReplHost` refuses to start otherwise, since anyone able to run the slash command would otherwise get a REPL with the same code-execution privileges as the host process:

- `AllowedUserIds` / `AllowedChannelIds`: `HashSet<string>?` of Slack user/channel IDs allowed to start and use sessions. Leaving one unset doesn't restrict by it — e.g. setting only `AllowedChannelIds` allows any user in those channels.
- `RestrictRepliesToSessionOwner` (default `true`): only the user who ran the slash command can send messages into their own session's thread.
- `IsAuthorized`: optional `Func<SlackAuthorizationContext, bool>` for additional checks (e.g. against an external ACL), evaluated in addition to the allowlists above, not instead of them.
- `IdleTimeout`: optional `TimeSpan` — a session thread with no activity for this long is closed automatically, so an abandoned thread doesn't hold a `RoslynServices` alive forever.

Run `dotnet run -- slack` in `Mykeels.CSharpRepl.Sample` (with `SLACK_BOT_TOKEN`, `SLACK_APP_TOKEN`, and optionally `SLACK_ALLOWED_CHANNEL_IDS` set as a comma-separated list) to try it.

## Features

- **Syntax Highlighting**: Code is colorized for better readability
- **Code Completion**: Intelligent code completion with IntelliSense
- **Error Handling**: Detailed error messages with stack traces
- **JSON Output**: Results are automatically serialized to indented, syntax-highlighted JSON
- **Result History**: Browse recent results in a scrollable JSON viewer with `Ctrl+O`
- **Customizable**: Configure references, namespaces, and output formatting
- **Interactive**: Full C# interactive environment with REPL capabilities

## Configuration Options

The `Configuration` class supports the following options:

- `references`: Array of assembly references to load
- `usings`: Array of namespaces to import by default
- `applicationName`: Name of your application
- `logSuccess`: Callback for handling successful evaluations
- `logError`: Callback for handling evaluation errors
- `commands`: Array of commands to execute before starting the REPL

## Examples

### Basic Usage

```csharp
await Repl.Run();
```

### With Custom References

```csharp
await Repl.Run(
    new Configuration(
        references: ["MyApp.dll", "MyApp.Models.dll"]
    )
);
```

### With Custom Output Formatting

```csharp
await Repl.Run(
    new Configuration(
        logSuccess: (message, result) => {
            Console.WriteLine($"Input: {message}");
            Console.WriteLine($"Result: {result}");
        }
    )
);
```

### With Pre-execution Commands

```csharp
await Repl.Run(
    commands: [
        "using static Mykeels.CSharpRepl.Sample.ScriptGlobals;",
        "var greeting = \"Hello, World!\";"
    ]
);
```

## Best Practices

1. **Assembly References**: Include all necessary assemblies in the `references` array
2. **Namespaces**: Add commonly used namespaces to the `usings` array
3. **Error Handling**: Implement custom error handling in `logError` for better debugging
4. **Output Formatting**: Use `AnsiConsole` for colored output and better readability
5. **Pre-execution Commands**: Use `commands` to set up your environment and import commonly used types

## License

Mozilla Public License 2.0 — the full text is in [LICENSE](LICENSE). Every source file, upstream or added here,
carries the MPL header. Third-party components keep their own licenses; see the package references in
`Mykeels.CSharpRepl/Mykeels.CSharpRepl.csproj`.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
