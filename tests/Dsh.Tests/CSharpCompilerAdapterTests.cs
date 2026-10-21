using Dsh.Lsp.CSharp;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Tests;

public sealed class CSharpCompilerAdapterTests
{
    [Fact]
    public async Task RoslynProvider_ProducesCstWithTriviaAndMissingTokens()
    {
        var provider = new CSharpSyntaxTreeProvider();
        var document = LspDocument.Create(
            "file:///probe.cs",
            "csharp",
            "class C {\n  // note\n  int x = ;\n}\n");
        var cst = await provider.TryParseAsync(document);
        Assert.NotNull(cst);
        Assert.Contains(cst.Descendants(), node => node.IsExtra);
        Assert.Contains(cst.Descendants(), node => node.IsMissing);
    }

    [Fact]
    public async Task RoslynTypeProvider_ReturnsStructuredType()
    {
        const string text = "class C { int X; void M(int a) { var b = a; } }";
        var document = LspDocument.Create("file:///types.cs", "csharp", text);
        var provider = new CSharpTypeProvider();
        var start = text.IndexOf('X', StringComparison.Ordinal);
        var result = await provider.ResolveAsync(
            document,
            new LspRange(new LspPosition(0, start), new LspPosition(0, start + 1)));
        Assert.NotNull(result);
        Assert.Equal(TypeExpressionKind.Primitive, result.Kind);
        Assert.Equal("int", result.Name);
    }

    [Fact]
    public async Task RoslynFlowProvider_BuildsControlFlowGraph()
    {
        const string text = "class C { void M(int a) { if (a > 0) return; var b = a; } }";
        var document = LspDocument.Create("file:///flow.cs", "csharp", text);
        var provider = new CSharpFlowProvider();
        var start = text.IndexOf('M', StringComparison.Ordinal);
        var flow = await provider.BuildAsync(
            document,
            new LspRange(new LspPosition(0, start), new LspPosition(0, start + 1)));
        Assert.NotNull(flow);
        Assert.NotEmpty(flow.Nodes);
        Assert.NotEmpty(flow.Edges);
    }

    [Fact]
    public async Task RoslynReferenceProvider_FindsCrossFileReferences()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "csharp-reference-fixture");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "Source.cs");
        var targetPath = Path.Combine(root, "Target.cs");
        const string source = "class Source { int M() { return Target.Value; } }";
        const string target = "static class Target { public const int Value = 1; }";
        await File.WriteAllTextAsync(sourcePath, source);
        await File.WriteAllTextAsync(targetPath, target);
        var document = LspDocument.Create(LspDocumentUri.FromPath(sourcePath), "csharp", source);
        var start = source.IndexOf("Value", StringComparison.Ordinal);
        var provider = new CSharpReferenceProvider();
        var edges = await provider.ResolveAsync(
            root,
            document,
            new LspRange(new LspPosition(0, start), new LspPosition(0, start + 5)));
        Assert.NotEmpty(edges);
        Assert.Contains(edges, edge => edge.Target.Uri.Contains("Source.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(edges, edge => edge.Kind == SymbolEdgeKind.Definition
            && edge.Target.Uri.Contains("Target.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RoslynProjectContext_LoadsProjectReferencesWithMSBuild()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "csharp-msbuild-fixture-" + Guid.NewGuid().ToString("N"));
        var lib = Path.Combine(root, "Lib");
        var app = Path.Combine(root, "App");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(
            Path.Combine(lib, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(lib, "Lib.cs"),
            "namespace Lib; public static class Values { public const int Answer = 42; }");
        await File.WriteAllTextAsync(
            Path.Combine(app, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(app, "App.cs"),
            "namespace App; public static class Program { public static int Value => Lib.Values.Answer; }");

        var context = await CSharpProjectContext.LoadAsync(app);

        Assert.True(context.IsMsBuild, CSharpProjectContext.LastError);
        Assert.Contains(context.SourceFiles, path => path.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(context.RoslynProject!.ProjectReferences);
        Assert.Contains(context.References, reference => reference.Display?.Contains("System.Runtime") == true);
    }
}
