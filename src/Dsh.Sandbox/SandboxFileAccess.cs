using Dsh.Transport;

namespace Dsh.Sandbox;

/** File capabilities address ordinary files; the host process, devices and kernel control interfaces remain outside this API. */
public static class SandboxFileAccess
{
    public static void DemandOrdinaryFile(string path, bool allowMissing)
    {
        global::Dsh.Runtime.CanonicalFilePath.DemandFilePath(path);
        var exists = File.Exists(path);
        if (exists && !FilePathAccess.IsOrdinaryFile(path) || Directory.Exists(path))
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

}
