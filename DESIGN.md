# MCPRoslyn — Design

Read-only, Roslyn-powered MCP server that gives AI coding agents compiler-grade answers about C#/VB.NET solutions. Strictly read-only by construction: the server never writes to any analyzed file.

## Principles

1. **Semantic exactness over text search.** Only expose what grep *cannot* do well: references, implementations, hierarchies, diagnostics, content inside compiled assemblies. No file read/write/grep duplicates.
2. **Token economy.** Line-oriented compact output, counts before content, pagination (default `max_results=50`), signatures by default / bodies on request. Stay far below the 25k-token response cap.
3. **Symbols are addressed by name, not coordinates.** Every tool accepts a fuzzy fully-qualified name (`OrderService`, `MyApp.Orders.OrderService.Process`, `Process(int, string)`) or a documentation-comment ID (`M:MyApp.OrderService.Process(System.Int32)`). Ambiguity returns a disambiguation list whose entries are directly pastable back in. Optional `file` + `line` addressing where it helps.
4. **Never silently stale.** File watcher applies edits to the in-memory solution before every query; project-file changes flag the workspace stale and responses say so.
5. **Errors teach.** Every failure names the recovery action ("No solution loaded — call load_solution", "3 matches for 'Process': …").
6. **Honest limits.** Tool descriptions state what is not seen (reflection, DI containers, `dynamic`). `find_unused` and `call_hierarchy` explain their approximations.

## Tool surface (16)

| Tool | Purpose | Key params |
|---|---|---|
| `load_solution` | Load/switch/reload a solution or project (auto-discovers from project dir) | `path?`, `force_reload?` |
| `workspace_status` | Load state, projects/TFMs, skipped projects, workspace warnings, staleness | — |
| `search_symbols` | Fuzzy/camel-hump declaration search with kind & project filters | `query`, `kind?`, `project?`, `max_results?`, `page?` |
| `get_symbol` | Signature, containers, locations, XML docs, attributes, members (types), base/interfaces | `symbol` or `file`+`line`, `include_members?`, `include_docs?` |
| `find_references` | All references with read/write classification, grouped by file | `symbol`, `project?`, `include_snippets?`, `max_results?`, `page?` |
| `find_implementations` | Implementations / overrides / derived classes & interfaces | `symbol`, `kind?` (implementations\|overrides\|derived\|all) |
| `get_type_hierarchy` | Base chain + interfaces upward, derived types downward | `symbol`, `direction?` |
| `call_hierarchy` | Callers (reverse) or callees (forward), bounded depth | `symbol`, `direction?`, `depth?`, `max_results?`, `page?` |
| `get_file_outline` | Token-compact structure of one file: members + signatures + line spans | `file` |
| `get_diagnostics` | Compiler (and optionally analyzer) diagnostics, scoped | `scope` (file\|project\|solution), `target?`, `min_severity?`, `include_analyzers?`, `page?` |
| `get_project_graph` | Projects, project references, TFMs, output kinds, NuGet packages | `include_packages?` |
| `decompile` | Metadata-as-source for NuGet/BCL symbols (ICSharpCode.Decompiler); real source if available | `symbol` |
| `get_usage_examples` | Ranked real call-site snippets showing how an API is used | `symbol`, `max_examples?` |
| `find_unused` | Symbols with zero references in scope (private/internal by default; caveats stated) | `scope` (project\|type\|namespace), `target`, `include_public?` |
| `analyze_impact` | Speculative in-memory edit → diagnostics delta in dependents; or blast radius of a symbol | `file`+`new_content`, or `symbol` |
| `ping` | Liveness + version + workspace one-liner (cheap smoke test) | — |

## Architecture

```
Program.cs                 stdout shield (Console.SetOut→stderr) FIRST, stdio host, DI
Workspace/
  RoslynWorkspaceService   MSBuildWorkspace lifecycle, auto-discovery, background load,
                           own immutable Solution snapshot, staleness flags
  FileSync                 FileSystemWatcher → queued changes → Solution.WithDocumentText /
                           AddDocument/RemoveDocument applied lazily before each query
Symbols/
  SymbolResolver           fuzzy FQN → ISymbol (+ doc-ID round trip, candidates on ambiguity)
  SymbolFormat             SymbolDisplayFormats, XML-doc → plain text, compact locations
Analysis/
  UsageClassifier          read/write/name classification of reference locations (C#)
Decompilation/
  DecompilerService        assembly path via Compilation.GetMetadataReference, CSharpDecompiler
Tools/                     one static [McpServerToolType] class per tool group
```

Key implementation facts (verified against Roslyn 5.6):

- MSBuildWorkspace runs MSBuild **out-of-process** (BuildHost): .NET Core host for SDK-style projects (needs `dotnet` on PATH), net472 host for legacy .NET Framework projects (needs VS or Build Tools; Windows). `Microsoft.Build.Locator` is not needed and not referenced.
- MSBuildWorkspace **never restores NuGet**. `load_solution` detects missing `project.assets.json` and tells the agent to run `dotnet restore`.
- Load failures are silent unless `WorkspaceFailed` is hooked — we collect all workspace diagnostics and expose them in `workspace_status`.
- Multi-targeted projects appear once per TFM; results are deduplicated by file path + span, and project names shown as `Name (net8.0)`.
- The `Solution` model is immutable snapshots; incremental sync = `WithDocumentText`, which reparses one file and invalidates only downstream compilations.
- Symbol identity across calls: `GetDocumentationCommentId` / `DocumentationCommentId.GetFirstSymbolForDeclarationId`.
- `SymbolEqualityComparer.Default` everywhere; never `==` across compilations.

## Explicit non-goals (v1)

- Semantic/embedding search (evidence mixed; agent grep + exact symbol search covers most of it)
- Interprocedural data flow / taint (Roslyn public API stops at intra-method)
- Cross-repo indexing; refactoring/editing of any kind
- F# (separate compiler service) and C/C++ (not a Roslyn language)
