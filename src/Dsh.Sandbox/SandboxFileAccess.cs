namespace Dsh.Sandbox;

/** File capabilities address ordinary files, not the host process, devices or kernel control interfaces. */
public static class SandboxFileAccess
{
    public static void DemandOrdinaryFile(string path, bool allowMissing)
    {
        DemandFilePath(path);
        var exists = File.Exists(path);
        if (exists && !NativeFileIdentity.Get(path).IsRegular || Directory.Exists(path))
            throw new NotSupportedException("Plugin file capabilities require an ordinary file.");
        if (!exists && !allowMissing)
            throw new FileNotFoundException("The requested plugin file does not exist.");
        if (!OperatingSystem.IsLinux())
            return;
        var probe = exists ? path : Path.GetDirectoryName(path);
        while (probe is not null && !Directory.Exists(probe) && !File.Exists(probe))
            probe = Path.GetDirectoryName(probe);
        LinuxMountTable.DemandOrdinaryPath(probe ?? Path.GetPathRoot(path)!);
    }

    public static void DemandFilePath(string path)
    {
        if (OperatingSystem.IsWindows())
            RejectDeviceNamespace(path);
    }

    public static bool IsNetworkPath(string path)
        => OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal));

    private static void RejectDeviceNamespace(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.AsSpan(root.Length).Contains(':'))
            throw new NotSupportedException("Plugin file capabilities do not expose device namespaces or alternate data streams.");
    }
}
