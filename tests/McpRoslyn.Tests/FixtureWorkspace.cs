using System.Diagnostics;
using McpRoslyn.Decompilation;
using McpRoslyn.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpRoslyn.Tests;

/// <summary>Loads the fixture solution once for all integration tests.</summary>
public sealed class FixtureWorkspace : IAsyncLifetime
{
    public RoslynWorkspaceService Workspace { get; } = new(NullLogger<RoslynWorkspaceService>.Instance);

    public DecompilerService Decompiler { get; } = new(NullLogger<DecompilerService>.Instance);

    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MCPRoslyn.slnx")) || File.Exists(Path.Combine(dir.FullName, "MCPRoslyn.sln")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("Could not locate the repo root (MCPRoslyn.slnx) above " + AppContext.BaseDirectory);
        }
    }

    public static string SolutionPath => Path.Combine(RepoRoot, "tests", "fixtures", "FixtureApp", "FixtureApp.slnx");

    public async Task InitializeAsync()
    {
        // MSBuildWorkspace does not restore; make sure the fixture is restored (cheap when cached).
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(SolutionPath)!, "FixtureCore", "obj", "project.assets.json")))
        {
            var restore = Process.Start(new ProcessStartInfo("dotnet", $"restore \"{SolutionPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await restore.WaitForExitAsync();
            if (restore.ExitCode != 0)
                throw new InvalidOperationException("dotnet restore of the fixture failed: " + await restore.StandardError.ReadToEndAsync());
        }

        await Workspace.LoadAsync(SolutionPath, force: false, progress: null, CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Workspace.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("fixture")]
public class FixtureCollection : ICollectionFixture<FixtureWorkspace>;
