using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Dsh.Boot;

/** Creates private settings temporary files without exposing their contents before permissions are restricted. */
internal static class SettingsFilePermissions
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int WriteBufferSize = 4096;

    public static void Write(string path, string content)
    {
        using var writer = new StreamWriter(OperatingSystem.IsWindows()
            ? CreateWindows(path)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = OwnerReadWrite,
            }));
        writer.Write(content);
    }

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
