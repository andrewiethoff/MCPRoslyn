using McpRoslyn.Analysis;
using McpRoslyn.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace McpRoslyn.Tests;

/// <summary>
/// Fast, workspace-free unit tests for the pure semantic helpers (read/write classification and
/// symbol-query parsing) that back find_references and symbol resolution.
/// </summary>
public class SemanticUnitTests
{
    // Classifies the `occurrence`-th identifier token named `token` in `code`.
    private static string ClassifyAt(string code, string token, int occurrence)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var ids = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == token)
            .ToList();
        return UsageClassifier.Classify(root, ids[occurrence - 1].Span);
    }

    [Theory]
    // Indexed writes: the ARRAY/collection receiver is only read; the element is what's written.
    [InlineData("class C { int[] d = new int[2]; void M(){ d[0] = 5; } }", "r")]
    [InlineData("class C { int[] d = new int[2]; void M(){ d[0] += 1; } }", "r")]
    [InlineData("class C { int[] d = new int[2]; void M(){ d[0]++; } }", "r")]
    [InlineData("class C { int[] d = new int[2]; void N(out int v){ v = 0; } void M(){ N(out d[0]); } }", "r")]
    public void IndexedWrite_ReceiverClassifiedAsRead(string code, string expected)
    {
        // `d`: occurrence 1 is the field declaration, occurrence 2 is the receiver in the index op.
        Assert.Equal(expected, ClassifyAt(code, "d", 2));
    }

    [Fact]
    public void PlainFieldWrite_StillClassifiedAsWrite()
    {
        Assert.Equal("W", ClassifyAt("class C { int f; void M(){ f = 5; } }", "f", 2));
    }

    [Fact]
    public void TupleDeconstructionTarget_ClassifiedAsWrite()
    {
        Assert.Equal("W", ClassifyAt("class C { int a, b; void M(){ (a, b) = (1, 2); } }", "a", 2));
    }

    [Fact]
    public void NestedTupleDeconstructionTarget_ClassifiedAsWrite()
    {
        Assert.Equal("W", ClassifyAt("class C { int a, b, c; void M(){ (a, (b, c)) = (1, (2, 3)); } }", "b", 2));
    }

    [Fact]
    public void TupleOnRightHandSide_ClassifiedAsRead()
    {
        Assert.Equal("r", ClassifyAt("class C { int a; void M(){ var t = (a, 5); } }", "a", 2));
    }

    [Theory]
    // A parameter type ending in in/out/ref before the parameter-name space must survive intact.
    [InlineData("PluginHost.Load(Plugin plugin)", "Plugin")]
    [InlineData("Layout.Apply(Margin margin)", "Margin")]
    [InlineData("M.Do(out Timeout t)", "Timeout")]
    [InlineData("M.Do(in Domain d)", "Domain")]
    [InlineData("Calc.Add(System.Int64 value)", "System.Int64")]
    public void ParseQuery_KeepsTypeNamesContainingModifierSubstrings(string query, string expectedFirstParam)
    {
        var parsed = SymbolResolver.ParseQuery(query);
        Assert.NotNull(parsed.ParameterTypes);
        Assert.Equal(expectedFirstParam, parsed.ParameterTypes![0]);
    }
}
