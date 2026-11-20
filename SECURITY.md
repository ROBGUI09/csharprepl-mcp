# Security

## Reporting a vulnerability

Use GitHub's private vulnerability reporting — **Security → Report a vulnerability** on this repository — rather
than a public issue. A useful report has: the tool name, a minimal reproduction (the arguments you sent and what
you expected), and the commit or version you tested. Expect a first reply within a week.

Out of scope: anything that requires the caller to already have code execution or full filesystem access through
the server. In scope: a tool call whose input escapes the documented behaviour — a "read-only" analysis tool that
writes, path handling that leaves the working directory when it promises not to, output that leaks data from a
different session.

## What the server is, and what that implies

The MCP server is a code-execution surface by design. Treat any connected client as having the same rights as the
account the server runs under.

- `run_script`, `run_file`, `run_tests` and `add_project` compile and execute C#. The code sees the filesystem,
  the network and the environment the process inherited.
- `add_reference` and `--preload` load assemblies into the live process. A module initializer runs on load, so a
  preloaded DLL is code execution, not data.
- `attach_process`, `analyze_file`, `diff_assemblies` and the rest of the inspection tools open target files and
  processes read-only. They never inject code and never start the target, but they do read module lists, metadata
  and user strings from whatever path you point them at.
- `cwd` on a call is a convenience for resolving relative paths, **not** a sandbox. Nothing stops a script from
  using an absolute path or `..`.
- Captured stdout, returned values and saved result handles live in memory for the lifetime of the process;
  `reset_session` drops them.

There is no authentication, no network listener and no privilege boundary: the transport is stdio, so the parent
process decides who can talk to it. Run it as a user with only the rights you intend to grant, and never wire it
into a shell that untrusted input can reach.
