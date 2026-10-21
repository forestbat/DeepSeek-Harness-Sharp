using Dsh.Lsp;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class LspProbeTests
{
    [Fact]
    public void RoslynProbe_AcceptsRealAdapterInstallation()
    {
        var adapter = FindAdapter();
        Assert.NotNull(adapter);
        Assert.Null(RoslynProbe.CheckCompatibility(adapter, Path.GetDirectoryName(adapter)!));
    }

    [Fact]
    public void RoslynProbe_ReportsMissingRoslynAssemblies()
    {
        var adapter = FindAdapter();
        Assert.NotNull(adapter);
        var empty = Path.Combine(AppContext.BaseDirectory, "probe-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var error = RoslynProbe.CheckCompatibility(adapter, empty);
            Assert.NotNull(error);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void RoslynProbe_ReportsInvalidAdapter()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "invalid-adapter-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(path, "not a managed assembly");
        try
        {
            var error = RoslynProbe.CheckCompatibility(path, AppContext.BaseDirectory);
            Assert.Contains("not a", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoslynProbe_ReportsRoslynVersionMismatch()
    {
        var adapter = FindAdapter();
        Assert.NotNull(adapter);
        var sdk = FindRiderRoslyn();
        if (sdk is null)
            return;

        var error = RoslynProbe.CheckCompatibility(adapter, sdk);
        Assert.NotNull(error);
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoslynProbe_ReportsAdapterLoadFailure()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "probe-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Dsh.Lsp.CSharp.dll"), "not a managed assembly");
        File.WriteAllText(Path.Combine(directory, "Microsoft.CodeAnalysis.dll"), "not a managed assembly");
        File.WriteAllText(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.dll"), "not a managed assembly");
        try
        {
            var registration = RoslynProbe.TryLoad(new Context(), out var error, [directory]);
            Assert.Null(registration);
            Assert.NotNull(error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindAdapter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Dsh.Lsp.CSharp",
                "bin",
                "Debug",
                "net10.0",
                "Dsh.Lsp.CSharp.dll");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindRiderRoslyn()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var jetBrains = Path.Combine(programFiles, "JetBrains");
        if (!Directory.Exists(jetBrains))
            return null;
        foreach (var installation in Directory.EnumerateDirectories(jetBrains, "JetBrains Rider*"))
        {
            var sdk = Path.Combine(installation, "lib", "ReSharperHost", "windows-x64", "dotnet", "sdk");
            if (!Directory.Exists(sdk))
                continue;
            foreach (var version in Directory.EnumerateDirectories(sdk).OrderDescending())
            {
                var roslyn = Path.Combine(version, "Roslyn", "bincore");
                if (File.Exists(Path.Combine(roslyn, "Microsoft.CodeAnalysis.dll")))
                    return roslyn;
            }
        }

        return null;
    }
}
