# MCPRoslyn

**Read-only, Roslyn-powered MCP server that gives AI coding agents compiler-grade answers about C# / VB.NET solutions.**

Text search can't tell you who implements an interface, which of 300 `Process` matches is *your* overload, or what's inside a NuGet assembly. Roslyn — the actual C#/VB compiler — can. MCPRoslyn puts that power behind 16 token-efficient MCP tools, strictly read-only by construction: it never writes to any analyzed file.

## Highlights

- **Semantic exactness**: `find_references` understands overloads, extension methods, aliases and partials — and never matches comments or strings.
- **Sees inside assemblies**: `decompile` shows real source for NuGet/BCL APIs (metadata-as-source via ICSharpCode.Decompiler) — no more hallucinated library APIs.
- **Impact analysis**: `analyze_impact` applies an edit *in memory*, recompiles all dependent projects and reports exactly which new errors the change would introduce. Disk is never touched.
- **Fast diagnostics**: a warm workspace re-checks code in a fraction of `dotnet build` time.
- **Legacy-friendly**: loads modern SDK-style projects *and* old .NET Framework (non-SDK) `.csproj` — via the Roslyn out-of-process build hosts.
- **Never silently stale**: a file watcher applies your edits to the in-memory solution before every query.
- **Agent-first ergonomics**: symbols addressed by fuzzy fully-qualified names (never line/column), counts-first paginated output, errors that name the recovery action.

## Tools

| Tool | Answers |
|---|---|
| `search_symbols` | "Where is anything named …?" (substring + camel-hump, kind filters) |
| `get_symbol` | "What is X?" — signature, docs, attributes, members, overloads, locations |
| `find_references` | "Who uses X?" — with read/write flags for fields/properties |
| `find_implementations` | "Who implements/overrides/derives from X?" |
| `get_type_hierarchy` | Base chain + interfaces up, derived types down |
| `call_hierarchy` | Callers or callees, transitive to depth 3 |
| `get_file_outline` | Structure of a file in ~1% of its tokens |
| `get_usage_examples` | Real call-site examples for an API |
| `get_diagnostics` | Compiler (and optional analyzer) errors/warnings — file/project/solution |
| `get_project_graph` | Projects, references, TFMs, NuGet packages |
| `decompile` | Source of NuGet/BCL symbols |
| `find_unused` | Zero-reference symbols (with honest caveats) |
| `analyze_impact` | "What breaks if I change this?" — speculative in-memory edit or blast radius |
| `load_solution` / `workspace_status` / `ping` | Lifecycle & health |

## Requirements

- **.NET 10 SDK** (the server runs on it, and its build host loads SDK-style projects with it)
- For **legacy .NET Framework projects**: Visual Studio 2022+ or Build Tools installed (Windows) — Roslyn's `net472` build host uses their MSBuild
- Solutions should be **restored** (`dotnet restore`) before loading — MCPRoslyn never modifies your source or runs restore itself

## Install

**📖 Full step-by-step guide (every harness, exact commands, configuration): [docs/INSTALL.md](docs/INSTALL.md).**

Three ways to get the server, each giving a `command` + `args` you drop into any MCP host:

| How | command | args |
|---|---|---|
| From NuGet via `dnx` (once published; nothing to install) | `dnx` | `MCPRoslyn --yes` |
| Global .NET tool — `dotnet tool install --global MCPRoslyn` | `mcp-roslyn` | _(none)_ |
| From source — `dotnet build -c Release src/McpRoslyn` | `dotnet` | `<abs>/src/McpRoslyn/bin/Release/net10.0/McpRoslyn.dll` |

Quick start for **Claude Code** (see the guide for VS Code / Visual Studio / GitHub Copilot Coding Agent / Codex):

```bash
# from source today; swap in `dnx MCPRoslyn --yes` once the package is on NuGet
git clone https://github.com/andrewiethoff/MCPRoslyn && cd MCPRoslyn
dotnet build -c Release src/McpRoslyn
claude mcp add --scope user roslyn -- dotnet "$PWD/src/McpRoslyn/bin/Release/net10.0/McpRoslyn.dll"
```

On startup the server auto-discovers the solution at/above the working directory (Claude Code's project dir; other hosts' current folder) and loads it in the background — **no configuration needed** in the common case. To pin one explicitly set `MCPROSLYN_SOLUTION=<path>`, switch at runtime with the `load_solution` tool, or disable auto-load with `MCPROSLYN_AUTOLOAD=0`. Details in [docs/INSTALL.md](docs/INSTALL.md#4-which-solution-gets-analyzed-configuration).

## What it deliberately does NOT do

- **No editing, no refactoring, no shell** — read-only by construction; your agent's own tools do the writing. (One documented exception to *code execution*: `get_diagnostics` with `include_analyzers=true` runs the project's third-party Roslyn analyzer assemblies in-process. It is off by default and stays opt-in; MCPRoslyn itself still never writes to your files.)
- **No file reading/grep duplicates** — your agent already has better ones; MCPRoslyn only adds what grep can't do.
- **No embeddings/semantic search** — evidence for code is mixed; exact symbol search + your agent's grep covers most of it.
- **Reflection/DI-container/dynamic dispatch resolution** — impossible statically; tools state this caveat instead of guessing.
- **F# / C++** — not Roslyn languages. C# and VB.NET only.

## Architecture notes

- `MSBuildWorkspace` with out-of-process build hosts (Roslyn 5.6): `dotnet` on PATH loads SDK-style projects, an installed VS/Build Tools MSBuild loads legacy `.csproj`. `Microsoft.Build.Locator` is not used.
- The workspace is an immutable snapshot; a `FileSystemWatcher` feeds edits through `Solution.WithDocumentText` lazily before each query — one file re-parse, downstream-only recompilation. Watch roots cover every loaded document's directory, so linked/shared files declared outside a project folder stay in sync.
- Multi-targeted projects appear once per TFM; `find_*`/`search` results are deduplicated by physical declaration identity (so distinct projects that share a fully-qualified name are *not* collapsed), and `get_diagnostics`/`analyze_impact` compile **every** target framework so a TFM-specific break is not missed.
- Reference-assembly decompilation transparently falls back to the runtime implementation assembly (`System.Private.CoreLib` etc.) so BCL bodies are real, not `throw null` stubs.

## License

MIT
