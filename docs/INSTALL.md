# Installing & integrating MCPRoslyn

This guide covers getting the server onto a machine, wiring it into the common agent harnesses
(Claude Code, GitHub Copilot in VS Code / Visual Studio / the Coding Agent, OpenAI Codex), and how
the server decides which solution to analyze.

- [1. Prerequisites](#1-prerequisites)
- [2. Get the server (three ways)](#2-get-the-server-three-ways)
- [3. Integrate with your harness](#3-integrate-with-your-harness)
- [4. Which solution gets analyzed? (configuration)](#4-which-solution-gets-analyzed-configuration)
- [5. Verify it works](#5-verify-it-works)

---

## 1. Prerequisites

- **.NET 10 SDK or later** — the server runs on it, and it provides the `dnx` command used to run
  the server straight from NuGet. Check with:
  ```bash
  dotnet --info
  ```
- **Windows + Visual Studio 2022/2026 or Build Tools** — only needed if you analyze **legacy
  .NET Framework (non-SDK) projects**; Roslyn's `net472` build host uses their MSBuild.
- The solution you point it at should be **restored** first (`dotnet restore YourApp.slnx`).
  MCPRoslyn is strictly read-only: it never restores, builds, or writes to your files.

---

## 2. Get the server (three ways)

Each option gives you a **command + arguments** that you drop into a harness's MCP config in §3.

### Option A — Run from NuGet with `dnx` (recommended, no install)

Once the package is published to nuget.org, nothing to install — `dnx` downloads and runs it:

```bash
dnx MCPRoslyn --yes
```

- **command:** `dnx`  **args:** `["MCPRoslyn", "--yes"]`
- `--yes` auto-accepts the one-time package download. `dnx` always uses the latest published version.

### Option B — Install as a global .NET tool

```bash
dotnet tool install  --global MCPRoslyn      # install
dotnet tool update   --global MCPRoslyn      # upgrade later
dotnet tool uninstall --global MCPRoslyn     # remove
```

This creates a command named **`mcp-roslyn`** on your PATH. The executable lives at:

| OS | Path |
|---|---|
| Windows | `%USERPROFILE%\.dotnet\tools\mcp-roslyn.exe` |
| Linux / macOS | `~/.dotnet/tools/mcp-roslyn` |

- **command:** `mcp-roslyn`  **args:** _(none)_

### Option C — Build from source (works today, before the NuGet publish)

```bash
git clone https://github.com/andrewiethoff/MCPRoslyn
cd MCPRoslyn
dotnet build -c Release src/McpRoslyn
```

The server assembly is produced at:

```
src/McpRoslyn/bin/Release/net10.0/McpRoslyn.dll
```

Run it with the .NET host:

- **command:** `dotnet`  **args:** `["<abs-path>/src/McpRoslyn/bin/Release/net10.0/McpRoslyn.dll"]`

You can also pack a local NuGet package and install it as a global tool from a folder:

```bash
dotnet pack -c Release src/McpRoslyn                 # -> src/McpRoslyn/bin/Release/MCPRoslyn.0.1.0.nupkg
dotnet tool install --global --add-source src/McpRoslyn/bin/Release MCPRoslyn
```

> **`MCPRoslyn` is published on [nuget.org](https://www.nuget.org/packages/MCPRoslyn)**, so options
> A (`dnx`) and B (global tool) work everywhere — they're the recommended path. Option C (from
> source) is for local development or trying unreleased changes. New versions publish automatically:
> pushing a GitHub Release tagged `vX.Y.Z` runs `.github/workflows/publish.yml`, which packs and
> pushes to nuget.org via trusted publishing (OIDC, no stored API key).

---

## 3. Integrate with your harness

All examples below use the `dnx` form (Option A). To use a global-tool install instead, replace
`"command": "dnx", "args": ["MCPRoslyn","--yes"]` with `"command": "mcp-roslyn"` (no args); to use a
source build, replace with `"command": "dotnet", "args": ["<abs>/McpRoslyn.dll"]`.

### Claude Code (CLI)

```bash
# A) from NuGet
claude mcp add --scope user roslyn -- dnx MCPRoslyn --yes

# B) global tool
claude mcp add --scope user roslyn -- mcp-roslyn

# C) from source
claude mcp add --scope user roslyn -- dotnet "H:/CProjekte/MCPRoslyn/src/McpRoslyn/bin/Release/net10.0/McpRoslyn.dll"
```

- `--scope user` registers it for **all** your projects (use `--scope project` to share it via a
  repo's `.mcp.json`, or `--scope local` for just the current project).
- Claude Code sets `CLAUDE_PROJECT_DIR` to the project you're working in, so the server **auto-loads
  that project's solution** with no extra configuration.
- Manage/verify: `claude mcp list`, `claude mcp get roslyn`, `claude mcp remove roslyn`. After
  rebuilding a **source** install, restart the Claude Code session so it reloads the new DLL.

### GitHub Copilot — VS Code (agent mode)

Create **`.vscode/mcp.json`** in your C# repo (or run **Command Palette → "MCP: Add Server"**):

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dnx",
      "args": ["MCPRoslyn", "--yes"],
      "cwd": "${workspaceFolder}"
    }
  }
}
```

`"cwd": "${workspaceFolder}"` makes the server auto-discover the solution in your open folder. For a
machine-wide setup, use **Command Palette → "MCP: Open User Configuration"** and add the same
`servers` entry there. Confirm it loaded via the **Tools** icon in the Copilot Chat view.

### GitHub Copilot — Visual Studio 2022 (17.14+) / 2026

Add a server to **`%USERPROFILE%\.mcp.json`** (all solutions) or **`<SolutionDir>\.mcp.json`**
(one solution):

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dnx",
      "args": ["MCPRoslyn", "--yes"]
    }
  }
}
```

Visual Studio launches the server with the solution directory as the working directory, so it
auto-loads that solution. In Copilot Chat, click the **Tools** icon and confirm `roslyn` is listed.
If the wrong solution (or none) is picked, pin it with `MCPROSLYN_SOLUTION` — see §4.

### GitHub Copilot — Coding Agent (cloud, per repository)

In your repo: **Settings → Copilot → Coding agent → Model Context Protocol (MCP)**, add:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "local",
      "command": "dnx",
      "args": ["MCPRoslyn", "--yes"],
      "tools": ["*"],
      "env": {}
    }
  }
}
```

The coding agent runs in a fresh container, so it needs .NET 10 installed for `dnx` to exist. Add
**`.github/workflows/copilot-setup-steps.yml`** (the job **must** be named `copilot-setup-steps`):

```yaml
name: "Copilot Setup Steps"
on:
  workflow_dispatch:
  push: { paths: [.github/workflows/copilot-setup-steps.yml] }
jobs:
  copilot-setup-steps:
    runs-on: ubuntu-latest
    permissions: { contents: read }
    steps:
      - name: Install .NET 10
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.x'
```

### OpenAI Codex CLI

Add to **`~/.codex/config.toml`** (or a project-scoped `.codex/config.toml`):

```toml
[mcp_servers.roslyn]
command = "dnx"
args = ["MCPRoslyn", "--yes"]
```

…or from the CLI:

```bash
codex mcp add roslyn -- dnx MCPRoslyn --yes
```

Codex starts the server in the directory you launched `codex` from, so it auto-discovers that
project's solution. To pin a specific one, add an env table:

```toml
[mcp_servers.roslyn.env]
MCPROSLYN_SOLUTION = "H:\\CProjekte\\YourApp\\YourApp.slnx"
```

### Any other stdio MCP host

Configure a **local / stdio** server with one of the command+args forms from §2 and (optionally) an
`env` for `MCPROSLYN_SOLUTION`. The server speaks MCP over stdio and logs only to stderr, so stdout
stays a clean protocol channel.

---

## 4. Which solution gets analyzed? (configuration)

**Short answer: usually nothing to configure.** The server discovers the solution from the working
folder and loads it in the background on startup.

**Discovery rule.** It walks **up** from the working directory and loads the first `*.sln` / `*.slnx`
it finds; if there is none all the way up, it does the same walk for `*.csproj` / `*.vbproj`.

**Working directory.** That is `CLAUDE_PROJECT_DIR` when set (Claude Code sets it to your project),
otherwise the server process's current directory — which VS Code (`cwd: ${workspaceFolder}`), Visual
Studio, and Codex set to your project/solution folder. So opening MCPRoslyn inside a C# project
"just works": it finds and loads that project's solution and keeps it in sync as you edit files.

**When you do need to configure** (all optional, via environment variables on the server entry):

| Situation | What to do |
|---|---|
| Several candidate solutions in the tree, or the wrong one is loaded | Pin one: `MCPROSLYN_SOLUTION=<abs path to .sln/.slnx/.csproj>` |
| The host launches the server from a non-project directory | Pin `MCPROSLYN_SOLUTION`, or set the config's `cwd` |
| Analyze a different solution than the current folder | Pin `MCPROSLYN_SOLUTION`, or ask the agent to call the **`load_solution`** tool with a path |
| Don't auto-load on startup | `MCPROSLYN_AUTOLOAD=0`, then call `load_solution` explicitly |

**Setting an environment variable per harness:**

- **Claude Code:**
  ```bash
  claude mcp add --scope user roslyn -e MCPROSLYN_SOLUTION="H:/CProjekte/YourApp/YourApp.slnx" -- dnx MCPRoslyn --yes
  ```
- **VS Code / Visual Studio:** add an `"env"` object to the server entry:
  ```json
  "env": { "MCPROSLYN_SOLUTION": "H:/CProjekte/YourApp/YourApp.slnx" }
  ```
- **Codex:** use the `[mcp_servers.roslyn.env]` table shown in §3.

**Runtime lifecycle tools** (ask the agent to call these): `workspace_status` (what's loaded, TFMs,
warnings, staleness), `load_solution` (switch or force-reload a solution), `ping` (health check).

---

## 5. Verify it works

Ask the agent to run the **`ping`** tool — it should report something like
`mcp-roslyn … Loaded <your solution>`. Then **`workspace_status`** shows the loaded solution and its
projects. Finally try a real query, e.g.:

- "find implementations of `IShape`"
- "who references `OrderService.Process`?"
- "decompile `System.Text.Json.JsonSerializer.Serialize`"

If `workspace_status` shows nothing loaded, either the working directory had no discoverable solution
(pin `MCPROSLYN_SOLUTION`) or the solution wasn't restored (`dotnet restore` it and call
`load_solution` with `force_reload: true`).
