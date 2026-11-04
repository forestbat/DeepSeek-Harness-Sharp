using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Dsh.Transport;

/**
 * Files and directories that only the current OS user can open.
 * They carry local secrets such as endpoint tokens, so a file is restricted from the moment it exists instead of being tightened afterwards.
 */
public static class OwnerOnlyFiles
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;
    private const int WriteBufferSize = 4096;

    /** Creates the directory, or tightens an existing one, so that only the owner can list or enter it. */
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows profile directories already inherit an owner/SYSTEM/Administrators-only ACL.
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnerAll);
        File.SetUnixFileMode(path, OwnerAll);
    }

    /** Atomically creates an owner-only file; an existing path is never replaced. */
    public static FileStream Create(string path)
        => OperatingSystem.IsWindows() ? CreateWindows(path) : CreateUnix(path);

    /** Writes a new owner-only file, replacing whatever was at the path. */
    public static void Write(string path, string contents)
        => Write(path, Encoding.UTF8.GetBytes(contents));

    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        File.Delete(path);
        using var stream = Create(path);
        stream.Write(contents);
    }

    /**
     * Binds a Unix domain socket that only the owner can connect to.
     * The socket is created inside an owner-only directory, so nobody else can reach it in the moment before its own mode is narrowed to 0600.
     */
    [UnsupportedOSPlatform("windows")]
    public static Socket ListenUnixSocket(string socketPath, int backlog)
    {
        EnsureDirectory(Path.GetDirectoryName(socketPath)!);
        File.Delete(socketPath);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, OwnerReadWrite);
            listener.Listen(backlog);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateUnix(string path)
        => new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerReadWrite,
        });

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("cannot resolve the current Windows user");
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, WriteBufferSize, FileOptions.None, security);
    }
}
