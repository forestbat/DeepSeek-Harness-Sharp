using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Dsh.Plugins;
using Dsh.Plugins.Generator;

namespace Dsh.Tests;

/** 生成器行为:包名缺省从程序集名推导;无属性时的多实现诊断仍显式报错。 */
public class PluginGeneratorTests
{
    [Fact]
    public void Manifest_WithoutPackageAttribute_DerivesPackageFromAssemblyName()
    {
        var run = RunGenerator("""
            using Dsh.Plugins;
            using Dsh.Runtime;
            namespace Sample;
            public sealed class MyPlugin : IDshPlugin
            {
                public string[] Inject => [];
                public System.IDisposable Apply(Context ctx, object? config) => throw new System.NotImplementedException();
            }
            """);
        var manifest = run.GeneratedTrees.Single(tree => tree.FilePath.Contains("DshPluginManifest"));
        Assert.Contains("\"Sample.Plugin\"", manifest.GetText(TestContext.Current.CancellationToken).ToString());
    }

    [Fact]
    public void Manifest_MultiplePluginTypesWithoutAttribute_ReportsDshplugin001()
    {
        var run = RunGenerator("""
            using Dsh.Plugins;
            using Dsh.Runtime;
            namespace Sample;
            public sealed class FirstPlugin : IDshPlugin
            {
                public string[] Inject => [];
                public System.IDisposable Apply(Context ctx, object? config) => throw new System.NotImplementedException();
            }
            public sealed class SecondPlugin : IDshPlugin
            {
                public string[] Inject => [];
                public System.IDisposable Apply(Context ctx, object? config) => throw new System.NotImplementedException();
            }
            """);
        Assert.Contains(run.Diagnostics, diagnostic => diagnostic.Id == "DSHPLUGIN001");
    }

    [Fact]
    public void Manifest_SharedDependencyAttribute_EmitsSharedDependencies()
    {
        var run = RunGenerator("""
            using Dsh.Plugins;
            using Dsh.Runtime;
            [assembly: DshSharedDependency("Common.Lib")]
            namespace Sample;
            public sealed class MyPlugin : IDshPlugin
            {
                public string[] Inject => [];
                public System.IDisposable Apply(Context ctx, object? config) => throw new System.NotImplementedException();
            }
            """);
        var manifest = run.GeneratedTrees.Single(tree => tree.FilePath.Contains("DshPluginManifest"));
        var text = manifest.GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("SharedDependencies", text);
        Assert.Contains("\"Common.Lib\"", text);
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Sample.Plugin",
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver.Create(new DshPluginCatalogGenerator())
            .RunGenerators(compilation)
            .GetRunResult();    }

    private static MetadataReference[] References()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trusted
            .Concat([typeof(IDshPlugin).Assembly.Location, typeof(Dsh.Runtime.Context).Assembly.Location])
            .Distinct()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
