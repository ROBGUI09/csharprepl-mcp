#!/usr/bin/env python
"""End-to-end smoke test for the C# REPL MCP server.

Usage:
    python mcp-smoke.py <path to Mykeels.CSharpRepl.Sample.exe | .dll>

Speaks newline-delimited JSON-RPC 2.0 over stdio, exactly like a real MCP client,
and exercises every tool. Exits non-zero on the first failed group of checks.

Environment:
    SMOKE_SKIP_NETWORK=1   skip add_package (needs nuget.org)
    SMOKE_SKIP_BUILD=1     skip add_project (needs the .NET SDK)
    SMOKE_VERBOSE=1        echo every tool result
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

VERBOSE = os.environ.get("SMOKE_VERBOSE") == "1"
SKIP_NETWORK = os.environ.get("SMOKE_SKIP_NETWORK") == "1"
SKIP_BUILD = os.environ.get("SMOKE_SKIP_BUILD") == "1"

failures = []


def check(name, condition, detail=""):
    if condition:
        print("  ok   " + name)
    else:
        print("  FAIL " + name + (" :: " + detail if detail else ""))
        failures.append(name)


def section(title):
    print("\n" + title)


def plain(text):
    """Tool output quotes strings C#-style, so paths arrive with doubled backslashes."""
    return text.replace("\\\\", "\\").replace("\\\"", "\"")


class Server:
    def __init__(self, argv, cwd):
        self.process = subprocess.Popen(
            argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            cwd=cwd,
        )
        self.next_id = 0

    def _write(self, payload):
        self.process.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self._write(message)

    def request(self, method, params=None):
        self.next_id += 1
        wanted = self.next_id
        message = {"jsonrpc": "2.0", "id": wanted, "method": method}
        if params is not None:
            message["params"] = params
        self._write(message)

        while True:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout during " + method)
            text = line.decode("utf-8", "replace").strip()
            if not text:
                continue
            reply = json.loads(text)
            if reply.get("id") != wanted:
                continue
            if "error" in reply:
                raise RuntimeError(json.dumps(reply["error"], ensure_ascii=False))
            return reply["result"]

    def call(self, tool, **arguments):
        result = self.request("tools/call", {"name": tool, "arguments": arguments})
        content = result.get("content") or [{}]
        text = content[0].get("text", "")
        if VERBOSE:
            print("    [" + tool + "] " + text.replace("\r\n", " | ")[:400])
        return text, bool(result.get("isError"))

    def close(self):
        try:
            self.process.stdin.close()
        except Exception:
            pass
        try:
            self.process.wait(timeout=20)
        except Exception:
            self.process.kill()


def make_workspace():
    # Under the current directory, not %TEMP%: dotnet build writes obj/ next to the project,
    # and a locked-down host may not be allowed to write into the temp folder.
    root = tempfile.mkdtemp(prefix="csrepl-smoke-", dir=os.getcwd())
    scripts = os.path.join(root, "scripts")
    os.makedirs(scripts)
    with open(os.path.join(scripts, "data.txt"), "w", encoding="utf-8") as handle:
        handle.write("hello from csx folder")
    with open(os.path.join(scripts, "hello.csx"), "w", encoding="utf-8") as handle:
        handle.write('using System.IO;\n\nFile.ReadAllText("data.txt").Trim()\n')
    return root, scripts


def make_library(root):
    project = os.path.join(root, "SmokeLib")
    os.makedirs(project)
    with open(os.path.join(project, "SmokeLib.csproj"), "w", encoding="utf-8") as handle:
        handle.write(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            "  <PropertyGroup>\n"
            "    <TargetFramework>net8.0</TargetFramework>\n"
            "    <Nullable>enable</Nullable>\n"
            "  </PropertyGroup>\n"
            "</Project>\n"
        )
    with open(os.path.join(project, "Greeter.cs"), "w", encoding="utf-8") as handle:
        handle.write(
            "namespace SmokeLib;\n\n"
            "public static class Greeter\n"
            "{\n"
            '    public static string Hi(string name) => "hi " + name;\n'
            "}\n"
        )
    return project


def make_test_project(root, name, with_failure):
    """xunit.v3 runs on Microsoft.Testing.Platform, so no testhost is involved."""
    project = os.path.join(root, name)
    os.makedirs(project)
    with open(os.path.join(project, name + ".csproj"), "w", encoding="utf-8") as handle:
        handle.write(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            "  <PropertyGroup>\n"
            "    <TargetFramework>net8.0</TargetFramework>\n"
            "    <OutputType>Exe</OutputType>\n"
            "    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>\n"
            "    <Nullable>enable</Nullable>\n"
            "  </PropertyGroup>\n"
            "  <ItemGroup>\n"
            '    <PackageReference Include="xunit.v3" Version="1.*" />\n'
            "  </ItemGroup>\n"
            "</Project>\n"
        )

    body = (
        "using Xunit;\n\n"
        "namespace " + name + ";\n\n"
        "public class MathTests\n"
        "{\n"
        "    [Fact]\n"
        "    public void Adds() => Assert.Equal(2, 1 + 1);\n"
    )
    if with_failure:
        body += (
            "\n"
            "    [Fact]\n"
            "    public void Fails() => Assert.Equal(3, 1 + 1);\n"
        )
    body += "}\n"
    with open(os.path.join(project, "MathTests.cs"), "w", encoding="utf-8") as handle:
        handle.write(body)
    return project


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    target = os.path.abspath(sys.argv[1])
    argv = ["dotnet", target] if target.lower().endswith(".dll") else [target]
    server_dir = os.path.dirname(target)
    root, scripts = make_workspace()
    library = None if SKIP_BUILD else make_library(root)

    server = Server(argv, cwd=server_dir)
    try:
        section("protocol")
        info = server.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "mcp-smoke", "version": "1"},
            },
        )
        server.notify("notifications/initialized")
        check("initialize returns serverInfo", bool(info.get("serverInfo", {}).get("name")))

        listing = server.request("tools/list")
        tools = {tool["name"]: tool for tool in listing["tools"]}
        check("tool count >= 19", len(tools) >= 19, str(sorted(tools)))
        for name in (
            "run_script",
            "run_file",
            "add_reference",
            "add_package",
            "decompile",
            "history",
            "run_tests",
            "analyze_file",
            "diff_assemblies",
            "get_result",
            "list_results",
            "attach_process",
        ):
            check("tool " + name + " exists", name in tools)
        schema = tools["run_script"]["inputSchema"]["properties"]
        check("run_script takes cwd", "cwd" in schema)
        check("run_script takes save_as", "save_as" in schema)
        check("run_file takes cwd", "cwd" in tools["run_file"]["inputSchema"]["properties"])

        section("run_script and per-call cwd")
        text, is_error = server.call("run_script", code="1 + 1")
        check("evaluates a snippet", not is_error and text.startswith("2"), text)

        text, is_error = server.call("run_script", code="var x = 41; x + 1")
        check("keeps variables between calls", not is_error and text.startswith("42"), text)

        text, is_error = server.call(
            "run_script", code="Environment.CurrentDirectory", cwd=scripts
        )
        check("cwd switches the process directory", scripts.lower() in plain(text).lower(), text)

        text, is_error = server.call("run_script", code="Environment.CurrentDirectory")
        check("cwd is restored after the call", server_dir.lower() in plain(text).lower(), text)

        text, is_error = server.call(
            "run_script", code="1", cwd=os.path.join(root, "definitely", "not", "here")
        )
        check(
            "missing cwd is a tool error",
            is_error and "Working directory not found" in text,
            text,
        )

        section("run_file")
        text, is_error = server.call("run_file", path="scripts/hello.csx", cwd=root)
        check(
            "runs a .csx relative to cwd",
            not is_error and "hello from csx folder" in text,
            text,
        )

        section("reflection")
        text, is_error = server.call("session_state")
        check("session_state lists variables", "x" in text, text)

        text, is_error = server.call("history")
        check("history replays submissions", "#1" in text, text)

        if library is not None:
            text, is_error = server.call("add_project", path="SmokeLib", cwd=root)
            check("add_project builds", not is_error and "build: ok" in text, text)

            text, is_error = server.call("run_script", code='SmokeLib.Greeter.Hi("smoke")')
            check(
                "project types are usable right away",
                not is_error and "hi smoke" in text,
                text,
            )

            text, is_error = server.call("list_types", pattern="^Greeter$")
            check("list_types finds the type", "SmokeLib.Greeter" in text, text)

            text, is_error = server.call("describe_type", type="SmokeLib.Greeter")
            check("describe_type shows methods", "Hi(String" in text, text)

            dll = os.path.join(root, "SmokeLib", "bin", "Debug", "net8.0", "SmokeLib.dll")
            text, is_error = server.call("inspect_assembly", path=dll)
            check("inspect_assembly reads metadata", "SmokeLib" in text, text)

            text, is_error = server.call("decompile", type="SmokeLib.Greeter", path=dll)
            check("decompile emits C#", "hi " in text, text)

            text, is_error = server.call("list_references")
            check("list_references mentions the assembly", "SmokeLib.dll" in text, text)

            text, is_error = server.call("analyze_file", path=dll)
            check(
                "analyze_file describes the assembly",
                not is_error and "SmokeLib" in text and "namespaces" in text,
                text,
            )
            check(
                "analyze_file groups types by namespace",
                "SmokeLib —" in text,
                text,
            )

            text, is_error = server.call("diff_assemblies", old_path=dll, new_path=dll)
            check(
                "diff_assemblies of a file against itself is identical",
                not is_error and "identical" in text,
                text,
            )

            other_dll = os.path.join(server_dir, "Mykeels.CSharpRepl.Sample.dll")
            if os.path.exists(other_dll):
                text, is_error = server.call("diff_assemblies", old_path=dll, new_path=other_dll)
                check(
                    "diff_assemblies reports differing types",
                    not is_error and "verdict:" in text and "added types" in text,
                    text,
                )

            text, is_error = server.call("run_tests", path="SmokeLib", cwd=root, timeout_ms=180000)
            check(
                "run_tests reports status and output",
                "status:" in text and "output:" in text,
                text,
            )

        text, is_error = server.call("run_tests", path="does/not/exist")
        check(
            "run_tests on a missing path is a tool error",
            is_error and "not found" in text,
            text,
        )

        if not SKIP_NETWORK:
            make_test_project(root, "ProbeGreen", with_failure=False)
            text, is_error = server.call("run_tests", path="ProbeGreen", cwd=root, timeout_ms=300000)
            check(
                "run_tests reports a green run",
                not is_error and "status: passed" in text and "1 passed" in text,
                text,
            )

            make_test_project(root, "ProbeRed", with_failure=True)
            text, is_error = server.call("run_tests", path="ProbeRed", cwd=root, timeout_ms=300000)
            check(
                "run_tests reports a failing run as a result",
                not is_error and "status: failed" in text and "1 failed" in text,
                text,
            )

        if not SKIP_NETWORK:
            text, is_error = server.call("add_package", id="Newtonsoft.Json", version="13.0.4")
            check("add_package restores a package", not is_error and "lib/" in text, text)
            text, is_error = server.call(
                "run_script",
                code='Newtonsoft.Json.JsonConvert.SerializeObject(new { a = 1, b = "x" })',
            )
            check(
                "package types are usable",
                not is_error and '{"a":1,"b":"x"}' in plain(text),
                text,
            )

        section("result handles")
        text, is_error = server.call("run_script", code="new[] { 1, 2, 3 }.Sum()", save_as="total")
        check("save_as keeps the result", not is_error and "saved as 'total'" in text, text)

        text, is_error = server.call("get_result", name="total")
        check(
            "get_result reads it back",
            not is_error and "System.Int32" in text and "6" in text,
            text,
        )

        text, is_error = server.call("list_results")
        check("list_results shows the handle", "total" in text, text)

        text, is_error = server.call(
            "run_script", code="var bag = new List<int> { 1, 2 }; bag", save_as="bag"
        )
        check("save_as keeps a live object", not is_error and "List" in text, text)

        server.call("run_script", code="bag.Add(3); bag.Count")
        text, is_error = server.call("get_result", name="bag")
        check(
            "get_result sees later mutations",
            not is_error and "[2]" in text and "3" in text,
            text,
        )

        text, is_error = server.call("get_result", name="nope")
        check(
            "unknown handle is a tool error",
            is_error and "No stored result" in text,
            text,
        )

        section("processes")
        text, is_error = server.call("attach_process")
        check(
            "attach_process reads the current process",
            not is_error and "modules:" in text,
            text,
        )
        check("attach_process lists managed modules", "Mykeels.CSharpRepl" in text, text)

        text, is_error = server.call("attach_process", properties="Mykeels")
        check("attach_process resolves assembly identity", "1." in text, text)

        text, is_error = server.call("attach_process", process="no-such-process-xyz")
        check(
            "unknown process is a tool error",
            is_error and "No running process" in text,
            text,
        )

        section("reset")
        text, is_error = server.call("reset_session")
        check("reset_session succeeds", not is_error, text)

        text, is_error = server.call("run_script", code="x")
        check("reset drops variables", is_error and "CS0103" in text, text)

        text, is_error = server.call("list_results")
        check("reset clears result handles", "No stored results" in text, text)

        text, is_error = server.call("run_script", code="1 + 1")
        check("session still evaluates after reset", not is_error and text.startswith("2"), text)
    finally:
        server.close()
        shutil.rmtree(root, ignore_errors=True)

    print()
    if failures:
        print("FAILED: " + str(len(failures)) + " check(s)")
        for name in failures:
            print("  - " + name)
        return 1

    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
