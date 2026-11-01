using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

internal static class SandboxHostPaths
{
    private static readonly string[] CredentialNames = [".ssh", ".aws", ".azure", ".kube", ".gnupg", ".config/gcloud", ".config/op", "Library/Keychains", "AppData/Roaming/Microsoft/Credentials", "AppData/Local/Microsoft/Credentials"];
    private static readonly Lazy<string[]> Homes = new(FindHomes);
    private static readonly Lazy<string[]> CredentialDirectories = new(() => Homes.Value.SelectMany(home => CredentialNames.Select(name => Path.GetFullPath(Path.Combine(home, name)))).ToArray());

    public static bool ExposesHome(string path) => Homes.Value.Any(home => SandboxPolicy.Contains(path, home));
    public static bool IsCredentialPath(string path) => CredentialDirectories.Value.Any(directory => SandboxPolicy.Contains(directory, path) || SandboxPolicy.Contains(path, directory));

    private static string[] FindHomes()
    {
        var homes = new List<string>();
        var configured = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
            homes.Add(SandboxPolicy.CanonicalExistingPath(configured));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            foreach (var uid in new[] { getuid(), geteuid() }.Distinct())
                homes.Add(AccountHome(uid));
        if (homes.Count == 0)
            throw new PlatformNotSupportedException("The host account home cannot be identified safely for sandbox grants.");
        return homes.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static unsafe string AccountHome(uint uid)
    {
        const int bufferBytes = 64 * 1024;
        var buffer = new byte[bufferBytes];
        var entry = stackalloc byte[256];
        fixed (byte* data = buffer)
        {
            var error = getpwuid_r(uid, entry, data, (nuint)bufferBytes, out var result);
            if (error != 0 || result == 0)
                throw new PlatformNotSupportedException("The host account home lookup failed within its bounded buffer.");
            // Darwin passwd includes change/class fields before pw_dir; glibc's passwd does not.
            var offset = OperatingSystem.IsMacOS() ? 48 : nint.Size == 8 ? 32 : 20;
            var pointer = *(nint*)(entry + offset);
            var home = Marshal.PtrToStringUTF8(pointer);
            if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
                throw new PlatformNotSupportedException("The host account does not have an identifiable absolute home directory.");
            return Directory.Exists(home) ? SandboxPolicy.CanonicalExistingPath(home) : Path.GetFullPath(home);
        }
    }

    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern unsafe int getpwuid_r(uint uid, byte* entry, byte* buffer, nuint bytes, out nint result);
}
