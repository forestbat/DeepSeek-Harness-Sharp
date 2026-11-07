using System.Reflection.PortableExecutable;
using Dsh.Sandbox;

namespace Dsh.Plugins.Sandbox;

/** Only code is exposed to the worker; configuration and business files go through the broker. */
internal static class PluginSandboxLaunch
{
    internal static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan InitializationBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InspectionBudget = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static Dsh.Plugins.PluginForm InspectForm(string? artifactPath)
    {
        if (artifactPath is null)
            return Dsh.Plugins.PluginForm.CompiledIn;
        artifactPath = global::Dsh.Runtime.CanonicalFilePath.Canonicalize(artifactPath);
        global::Dsh.Runtime.FilePathAccess.DemandOrdinaryFile(artifactPath, allowMissing: false);
        if (!Path.GetExtension(artifactPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return Dsh.Plugins.PluginForm.NativeLibrary;
        try
        {
            using var input = File.OpenRead(artifactPath);
            using var image = new PEReader(input);
            return image.HasMetadata ? Dsh.Plugins.PluginForm.ManagedAssembly : Dsh.Plugins.PluginForm.NativeLibrary;
        }
        catch (BadImageFormatException)
        {
            return Dsh.Plugins.PluginForm.NativeLibrary;
        }
    }

    internal static SandboxProcessOptions Options(IReadOnlyList<string> code)
    {
        var executable = Environment.ProcessPath
            ?? throw new PlatformNotSupportedException("The current host executable cannot be located.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrEmpty(entry) || !File.Exists(entry))
                throw new PlatformNotSupportedException("The managed host entry assembly cannot be located.");
            arguments.Add(Path.GetFullPath(entry));
        }
        arguments.Add(PluginSandboxWorker.HostArgument);
        return new SandboxProcessOptions
        {
            Executable = executable,
            Arguments = arguments,
            RuntimePaths = code,
            WorkingDirectory = Path.GetPathRoot(Path.GetFullPath(executable)),
            AllowNetwork = false,
            AllowChildProcesses = false,
            Environment = new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = SandboxRuntimePaths.DotnetRoot(),
                ["DOTNET_EnableDiagnostics"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
        };
    }
}
