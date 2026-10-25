using System.Runtime.InteropServices;

namespace Dsh.Fleet.Iroh;

/** Finds an explicitly installed command; availability does not imply protocol compatibility or network readiness. */
public static partial class IrohSshComponent
{
    private const int ExecutePermission = 1;
    private const int UnixPathCapacity = 4096;

    public static string? FindExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var range in path.AsSpan().Split(Path.PathSeparator))
        {
            var directory = path.AsSpan()[range].Trim('"');
            if (directory.IsEmpty)
                continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Join(directory, OperatingSystem.IsWindows() ? "iroh-ssh.exe" : "iroh-ssh"));
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || Access(candidate, ExecutePermission) == 0))
                {
                    var resolved = ResolveExecutable(candidate);
                    if (resolved is not null)
                        return resolved;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unusable PATH entry does not hide later installed commands.
            }
        }
        return null;
    }

    public static string RequireExecutable()
        => FindExecutable() ?? throw new FileNotFoundException("iroh-ssh is not installed as an executable on PATH. Install a compatible command before using iroh; receiver enable requires server --ready-file support.");

    private static unsafe string? ResolveExecutable(string candidate)
    {
        if (OperatingSystem.IsWindows())
            return File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;
        // Receiver ownership uses the physical executable path, including directory symlinks.
        byte* resolved = stackalloc byte[UnixPathCapacity];
        return RealPath(candidate, resolved) == 0 ? null : Marshal.PtrToStringUTF8((nint)resolved);
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint RealPath(string path, byte* resolvedPath);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);
}
