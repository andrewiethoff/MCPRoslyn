using McpRoslyn.Tools;
using McpRoslyn.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpRoslyn.Tests;

[Collection("fixture")]
public class ToolIntegrationTests(FixtureWorkspace fixture)
{
    private RoslynWorkspaceService Ws => fixture.Workspace;
    private static readonly NullLoggerFactory Lf = NullLoggerFactory.Instance;
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public void Ping_ReportsLoadedSolution()
    {
        var output = WorkspaceTools.Ping(Ws);
        Assert.Contains("mcp-roslyn", output);
        Assert.Contains("Loaded", output);
    }

    [Fact]
    public async Task WorkspaceStatus_ListsAllThreeProjects()
    {
        var output = await WorkspaceTools.WorkspaceStatus(Ws, Lf, Ct);
        Assert.Contains("FixtureCore", output);
        Assert.Contains("FixtureConsumer", output);
        Assert.Contains("FixtureVb", output);
        Assert.Contains("state: Loaded", output);
    }

    [Fact]
    public async Task SearchSymbols_FindsCircleWithCamelHump()
    {
        var output = await SearchTools.SearchSymbols(Ws, Lf, Ct, "Circle");
        Assert.Contains("class FixtureCore.Circle", output);

        var camelHump = await SearchTools.SearchSymbols(Ws, Lf, Ct, "ShEx");
        Assert.Contains("ShapeExtensions", camelHump);
    }

    [Fact]
    public async Task GetSymbol_DescribesTypeWithMembersAndDocs()
    {
        var output = await SearchTools.GetSymbol(Ws, Lf, Ct, symbol: "FixtureCore.Circle");
        Assert.Contains("class Circle", output);
        Assert.Contains("INamedShape", output);
        Assert.Contains("Area", output);
        Assert.Contains("ShapeBase", output);
    }

    [Fact]
    public async Task GetSymbol_ResolvesOverloadByParameterType()
    {
        var output = await SearchTools.GetSymbol(Ws, Lf, Ct, symbol: "Processor.Process(string)");
        Assert.Contains("Process(string value)", output);
        Assert.Contains("overloads (1)", output);
    }

    [Fact]
    public async Task GetSymbol_AmbiguousNameTeachesCandidates()
    {
        var output = await SearchTools.GetSymbol(Ws, Lf, Ct, symbol: "Process");
        Assert.StartsWith("ERROR:", output);
        Assert.Contains("ambiguous", output);
        Assert.Contains("M:FixtureCore.Processor.Process(System.Int32)", output);
    }

    [Fact]
    public async Task FindReferences_ClassifiesReadsAndWrites()
    {
        var output = await NavigationTools.FindReferences(Ws, Lf, Ct, "Processor.Counter");
        Assert.Matches(@":\d+ W ", output);   // Counter = 1 / = 10
        Assert.Matches(@":\d+ rw ", output);  // Counter += 1
        Assert.Matches(@":\d+ n ", output);   // nameof(Processor.Counter)
        Assert.Contains("Program.cs", output);
        Assert.Contains("Processor.cs", output);
    }

    [Fact]
    public async Task FindImplementations_CrossesLanguages()
    {
        var output = await NavigationTools.FindImplementations(Ws, Lf, Ct, "IShape");
        Assert.Contains("Circle", output);
        Assert.Contains("Square", output);
        Assert.Contains("VbShape", output); // VB project implementing a C# interface
        Assert.Contains("INamedShape", output); // derived interface
    }

    [Fact]
    public async Task FindImplementations_OfInterfaceMember()
    {
        var output = await NavigationTools.FindImplementations(Ws, Lf, Ct, "IShape.Area");
        Assert.Contains("ShapeBase.Area", output);
        Assert.Contains("VbShape.Area", output);
    }

    [Fact]
    public async Task TypeHierarchy_ShowsBothDirections()
    {
        var output = await NavigationTools.GetTypeHierarchy(Ws, Lf, Ct, "ShapeBase");
        Assert.Contains("implements", output);
        Assert.Contains("IShape", output);
        Assert.Contains("Circle", output);
        Assert.Contains("Square", output);
    }

    [Fact]
    public async Task CallHierarchy_FindsCallersOfOverload()
    {
        var output = await NavigationTools.CallHierarchy(Ws, Lf, Ct, "Processor.Process(int)");
        Assert.Contains("CallProcess", output);
    }

    [Fact]
    public async Task CallHierarchy_Callees()
    {
        var output = await NavigationTools.CallHierarchy(Ws, Lf, Ct, "ShapeExtensions.TotalArea", direction: "callees");
        Assert.Contains("IShape.Area", output); // call inside the lambda
        Assert.Contains("Sum", output);         // metadata callee (System.Linq)
    }

    [Fact]
    public async Task FileOutline_ShowsStructureWithLineRanges()
    {
        var output = await SearchTools.GetFileOutline(Ws, Lf, Ct, "Shapes.cs");
        Assert.Contains("interface IShape", output);
        Assert.Contains("class Circle", output);
        Assert.Contains("TotalArea", output);
        Assert.Matches(@"\[\d+-\d+\]", output);
    }

    [Fact]
    public async Task Diagnostics_FindTheDeliberateUnusedField()
    {
        var output = await AnalysisTools.GetDiagnostics(Ws, Lf, Ct, min_severity: "warning");
        Assert.Contains("CS0169", output); // _unusedField is never used
    }

    [Fact]
    public async Task ProjectGraph_ShowsReferencesAndLanguages()
    {
        var output = await AnalysisTools.GetProjectGraph(Ws, Lf, Ct);
        Assert.Contains("FixtureConsumer", output);
        Assert.Contains("refs: FixtureCore", output);
        Assert.Contains("VB", output);
    }

    [Fact]
    public async Task FindUnused_FindsDeliberateDeadCode()
    {
        var output = await AnalysisTools.FindUnused(Ws, Lf, Ct, target: "FixtureCore");
        Assert.Contains("UnusedHelper", output);
        Assert.Contains("_unusedField", output);
        Assert.DoesNotContain("IncrementTwice", output);
        // Interface implementations must not be flagged even with zero direct calls.
        Assert.DoesNotContain("Circle.Area", output);
    }

    [Fact]
    public async Task AnalyzeImpact_BlastRadius()
    {
        var output = await AnalysisTools.AnalyzeImpact(Ws, Lf, Ct, symbol: "IShape.Area");
        Assert.Contains("blast radius", output);
        Assert.Contains("references:", output);
        Assert.Contains("implementations", output);
    }

    [Fact]
    public async Task AnalyzeImpact_BenignLineShiftIsNotABreak()
    {
        // Inserting a blank line shifts every subsequent line — but introduces no real problem.
        // A line-sensitive diff would misreport the pre-existing CS0169 as resolved-and-reintroduced.
        var path = Path.Combine(Path.GetDirectoryName(FixtureWorkspace.SolutionPath)!, "FixtureCore", "Processor.cs");
        var original = await File.ReadAllTextAsync(path, Ct);
        var shifted = "\n" + original;

        var output = await AnalysisTools.AnalyzeImpact(Ws, Lf, Ct, file: "Processor.cs", new_content: shifted);
        Assert.Contains("✔", output);
        Assert.DoesNotContain("✖", output);
        // Real file untouched (in-memory only).
        Assert.Equal(original, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task Decompile_MetadataOverloadIsExact()
    {
        // Convert.ToInt32 has ~20 same-arity overloads. Matching by name+arity alone would return
        // arbitrary siblings; matching by documentation id returns exactly the bool overload,
        // whose body is the distinctive ternary 'value ? 1 : 0'.
        var output = await DecompileTools.Decompile(Ws, fixture.Decompiler, Lf, Ct, "System.Convert.ToInt32(bool)");
        Assert.Contains("ToInt32", output);
        Assert.Contains("bool", output);
        Assert.Contains("1 : 0", output);
        Assert.DoesNotContain("ToInt32(string", output);
        Assert.DoesNotContain("ToInt32(decimal", output);
    }

    [Fact]
    public async Task AnalyzeImpact_SpeculativeBreakingEdit()
    {
        var original = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(FixtureWorkspace.SolutionPath)!, "FixtureCore", "Shapes.cs"), Ct);
        var broken = original.Replace("double Area();", "double AreaRenamed();");

        var output = await AnalysisTools.AnalyzeImpact(Ws, Lf, Ct, file: "Shapes.cs", new_content: broken);
        Assert.Contains("✖", output);
        Assert.Contains("FixtureVb", output); // dependent VB project breaks: VbShape no longer implements IShape
        // And the real file was not touched:
        var onDisk = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(FixtureWorkspace.SolutionPath)!, "FixtureCore", "Shapes.cs"), Ct);
        Assert.Equal(original, onDisk);
    }

    [Fact]
    public async Task UsageExamples_ExtractsRealCallSites()
    {
        var output = await NavigationTools.GetUsageExamples(Ws, Lf, Ct, "ShapeExtensions.TotalArea");
        Assert.Contains("Program.cs", output);
        Assert.Contains("TotalArea()", output);
    }

    [Fact]
    public async Task Decompile_BclTypeFromRuntimeAssembly()
    {
        var output = await DecompileTools.Decompile(Ws, fixture.Decompiler, Lf, Ct, "System.Random");
        Assert.Contains("class Random", output);
        Assert.Contains("decompiled from", output);
    }

    [Fact]
    public async Task Decompile_SolutionSymbolReturnsRealSource()
    {
        var output = await DecompileTools.Decompile(Ws, fixture.Decompiler, Lf, Ct, "FixtureCore.Square");
        Assert.Contains("source (not decompiled)", output);
        Assert.Contains("Side * Side", output);
    }

    [Fact]
    public async Task GetSymbol_ResolvesOverloadByFullyQualifiedPrimitive()
    {
        // "System.Int64" must map to the keyword 'long' just like "System.Int32" maps to 'int'.
        var output = await SearchTools.GetSymbol(Ws, Lf, Ct, symbol: "Processor.Scale(System.Int64)");
        Assert.Contains("Scale", output);
        Assert.Contains("long", output);
    }

    [Fact]
    public async Task Decompile_NativeIntOverloadIsNotSilentlyWrong()
    {
        // Math.Max(nint,nint): the decompiler renders native int as 'nint' while Roslyn's doc-id
        // uses System.IntPtr, so the id match misses — the parameter-type fallback must still pick
        // the nint overload, not the arbitrary byte/decimal/double siblings.
        var output = await DecompileTools.Decompile(Ws, fixture.Decompiler, Lf, Ct, "System.Math.Max(nint, nint)");
        Assert.Contains("nint", output);
        Assert.DoesNotContain("decimal", output);
        Assert.DoesNotContain("double", output);
    }

    [Fact]
    public async Task AnalyzeImpact_AddingADuplicateDiagnosticIsCounted()
    {
        // Warnings.cs already has one CS0219 (unused 'x'); adding a second method with an identically
        // worded CS0219 is a real new warning. A set-based diff would hide it behind the same key.
        var newContent = """
            namespace FixtureCore;

            public static class Warnings
            {
                public static void A()
                {
                    int x = 1;
                }
                public static void B()
                {
                    int x = 1;
                }
            }
            """;
        var output = await AnalysisTools.AnalyzeImpact(Ws, Lf, Ct, file: "Warnings.cs", new_content: newContent);
        Assert.Contains("✖", output);
        Assert.Contains("CS0219", output);
    }
}
