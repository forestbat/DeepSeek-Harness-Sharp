using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Dsh.Plugins;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class VerifiedPluginLoaderTests
{
    [Fact]
    public void FrozenSharedClosure_OverridesConflictingGlobalSharedDirectory()
    {
        using var fixture = new Fixture();
        var global = fixture.Deployment("global/.shared", "global", "global-leaf");
        var frozen = fixture.Deployment("frozen", "frozen", "frozen-leaf");
        var host = fixture.Host(Path.GetDirectoryName(global.Entry)!);
        var plugin = fixture.Load(host, fixture.Bundle(frozen));

        Assert.Equal("frozen:frozen-leaf", Read(plugin));
        Assert.True(File.Exists(frozen.Marker));
        Assert.False(File.Exists(global.Marker));
        Assert.NotEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(Shared(plugin).GetType().Assembly));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnknownCachedSharedIdentity_IsRejectedBeforeEntryCode(bool transitive, bool equalBytes)
    {
        using var fixture = new Fixture();
        var cached = fixture.Deployment("cached/.shared", equalBytes ? "frozen" : "cached", equalBytes ? "frozen-leaf" : "cached-leaf");
        var frozen = fixture.Deployment("frozen", "frozen", "frozen-leaf");
        var host = fixture.Host(Path.GetDirectoryName(cached.Entry)!);
        var file = transitive ? cached.Leaf : cached.Shared;
        var assembly = host.SharedPool!.ResolveOrLoad(AssemblyName.GetAssemblyName(file), () => file);
        Assert.NotNull(assembly);

        Assert.Throws<FileLoadException>(() => host.TryLoadVerified(fixture.Bundle(frozen)));
        Assert.False(File.Exists(frozen.Marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentVerifiedSharedBytes_AreRejectedEvenAfterOriginalPathIsReplaced(bool transitive)
    {
        using var fixture = new Fixture();
        var first = fixture.Deployment("first", "first", "first-leaf");
        var second = fixture.Deployment("second", transitive ? "first" : "second", transitive ? "second-leaf" : "first-leaf");
        var host = fixture.Host(Path.Combine(fixture.Root, ".shared"));
        Assert.Equal("first:first-leaf", Read(fixture.Load(host, fixture.Bundle(first))));
        File.Copy(transitive ? second.Leaf : second.Shared, transitive ? first.Leaf : first.Shared, overwrite: true);

        Assert.Throws<FileLoadException>(() => host.TryLoadVerified(fixture.Bundle(second)));
        Assert.False(File.Exists(second.Marker));
    }

    [Fact]
    public void EqualVerifiedBytes_ShareActualTypesAcrossPluginArtifacts()
    {
        using var fixture = new Fixture();
        var first = fixture.Deployment("first", "equal", "equal-leaf");
        var second = fixture.Deployment("second", "equal", "equal-leaf");
        var host = fixture.Host(Path.Combine(fixture.Root, ".shared"));
        var firstPlugin = fixture.Load(host, fixture.Bundle(first));
        var secondPlugin = fixture.Load(host, fixture.Bundle(second));

        Assert.Equal("equal:equal-leaf", Read(firstPlugin));
        Assert.Equal("equal:equal-leaf", Read(secondPlugin));
        Assert.Same(Shared(firstPlugin).GetType(), Shared(secondPlugin).GetType());
        Assert.Same(Invoke(firstPlugin, "LeafType"), Invoke(secondPlugin, "LeafType"));
        Assert.NotSame(firstPlugin.GetType().Assembly, secondPlugin.GetType().Assembly);
    }

    [Fact]
    public void CapturedManagedBytes_AreNotReopenedFromMutableSnapshotPaths()
    {
        using var fixture = new Fixture();
        var original = fixture.Deployment("original", "original", "original-leaf");
        var replacement = fixture.Deployment("replacement", "replacement", "replacement-leaf");
        var bundle = fixture.Bundle(original);
        File.Copy(replacement.Shared, original.Shared, overwrite: true);
        File.Copy(replacement.Leaf, original.Leaf, overwrite: true);
        var host = fixture.Host(Path.Combine(fixture.Root, ".shared"));

        Assert.Equal("original:original-leaf", Read(fixture.Load(host, bundle)));
        Assert.False(File.Exists(replacement.Marker));
    }

    [Fact]
    public void MissingTransitiveFrozenDependency_CannotFallBackToGlobalSharedDirectory()
    {
        using var fixture = new Fixture();
        var global = fixture.Deployment("global/.shared", "global", "global-leaf");
        var frozen = fixture.Deployment("frozen", "frozen", "frozen-leaf");
        var host = fixture.Host(Path.GetDirectoryName(global.Entry)!);
        var bundle = fixture.Bundle(frozen, includeLeaf: false);

        Assert.Throws<FileNotFoundException>(() => host.TryLoadVerified(bundle));
        Assert.False(File.Exists(frozen.Marker));
    }

    private static object Invoke(IDshPlugin plugin, string method)
        => plugin.GetType().GetMethod(method)!.Invoke(plugin, null)!;
    private static string Read(IDshPlugin plugin) => (string)Invoke(plugin, "Read");
    private static object Shared(IDshPlugin plugin) => Invoke(plugin, "Shared");

    private sealed record Deployment(string Entry, string Shared, string Leaf, string Package, string Marker);

    private sealed class Fixture : IDisposable
    {
        private static readonly Lazy<MetadataReference[]> PlatformReferences = new(() =>
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted.Concat([typeof(IDshPlugin).Assembly.Location, typeof(Dsh.Runtime.Context).Assembly.Location])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
        });

        private readonly string _suffix = Guid.NewGuid().ToString("N");
        private readonly List<(PluginHost Host, PluginLoadResult Result)> _loaded = [];
        private readonly List<SharedAssemblyPool> _pools = [];
        internal string Root { get; } = TempTree.CreateDirectory("verified-loader");
        private string SharedName => "VerifiedShared_" + _suffix;
        private string LeafName => "VerifiedLeaf_" + _suffix;

        internal Deployment Deployment(string directory, string value, string leafValue)
        {
            var target = Path.Combine(Root, directory);
            Directory.CreateDirectory(target);
            var leaf = Path.Combine(target, LeafName + ".dll");
            Compile(LeafName, $$"""
                namespace LoaderFixture;
                public sealed class LeafToken { public static string Read() => "{{leafValue}}"; }
                """, leaf);
            var shared = Path.Combine(target, SharedName + ".dll");
            Compile(SharedName, $$"""
                namespace LoaderFixture;
                public sealed class SharedToken
                {
                    public static string Read() => "{{value}}:" + LeafToken.Read();
                    public static System.Type LeafType() => typeof(LeafToken);
                }
                """, shared, leaf);
            var entryName = "VerifiedPlugin_" + Guid.NewGuid().ToString("N");
            var entry = Path.Combine(target, entryName + ".dll");
            var marker = Path.Combine(target, "entry-executed.txt");
            var package = "test/verified-" + entryName;
            var markerLiteral = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(marker, quote: true);
            Compile(entryName, $$"""
                using Dsh.Plugins;
                using Dsh.Runtime;
                [assembly: DshSharedDependency("{{SharedName}}")]
                namespace LoaderFixture
                {
                    public sealed class Plugin : IDshPlugin
                    {
                        public string[] Inject => [];
                        public System.IDisposable Apply(Context context, object? config) => new Lifetime();
                        public string Read() => SharedToken.Read();
                        public object Shared() => new SharedToken();
                        public System.Type LeafType() => SharedToken.LeafType();
                        private sealed class Lifetime : System.IDisposable { public void Dispose() {} }
                    }
                    public static class Startup
                    {
                        [System.Runtime.CompilerServices.ModuleInitializer]
                        public static void Initialize() => System.IO.File.WriteAllText({{markerLiteral}}, "executed");
                    }
                }
                namespace Dsh.Plugins.Generated
                {
                    public static class DshPluginManifest
                    {
                        public static System.Collections.Generic.IReadOnlyList<DshPluginRegistration> Registrations()
                            => [new("{{package}}", PluginDescriptor.CurrentVersion, () => new LoaderFixture.Plugin())];
                        public static System.Collections.Generic.IReadOnlyList<string> SharedDependencies() => ["{{SharedName}}"];
                    }
                }
                """, entry, shared, leaf);
            return new Deployment(entry, shared, leaf, package, marker);
        }

        internal VerifiedPluginBundle Bundle(Deployment deployment, bool includeLeaf = true)
        {
            var files = includeLeaf ? new[] { deployment.Entry, deployment.Shared, deployment.Leaf }
                : [deployment.Entry, deployment.Shared];
            return new VerifiedPluginBundle(deployment.Entry, files.ToDictionary(path => path,
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
        }

        internal PluginHost Host(string sharedDirectory)
        {
            Directory.CreateDirectory(sharedDirectory);
            var pool = new SharedAssemblyPool(sharedDirectory);
            _pools.Add(pool);
            return new PluginHost { SharedPool = pool };
        }

        internal IDshPlugin Load(PluginHost host, VerifiedPluginBundle bundle)
        {
            var result = host.TryLoadVerified(bundle);
            _loaded.Add((host, result));
            Assert.Empty(result.Skipped);
            Assert.True(host.Catalog.TryGet(Assert.Single(result.Packages), out var create));
            return create!();
        }

        private static void Compile(string name, string source, string destination, params string[] dependencies)
        {
            MetadataReference[] references = dependencies.Length == 0 ? PlatformReferences.Value
                : [.. PlatformReferences.Value, .. dependencies.Select(path => MetadataReference.CreateFromFile(path))];
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            using var output = File.Create(destination);
            var result = compilation.Emit(output, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }

        public void Dispose()
        {
            foreach (var (host, result) in _loaded)
            {
                foreach (var package in result.Packages)
                    host.Catalog.Remove(package);
                host.SharedPool!.Release(result.SharedDependencies);
                result.Context?.Unload();
            }
            foreach (var pool in _pools)
                pool.Release([]);
            TempTree.Delete(Root);
        }
    }
}
