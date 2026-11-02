using Dsh.Sandbox;

namespace Dsh.Boot;

/** Compares canonical paths without promising hardlink alias or concurrent replacement protection. */
public static class SettingsFilePath
{
    public static bool SameFile(string left, string right)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return comparer.Equals(SandboxFileAccess.Canonicalize(left), SandboxFileAccess.Canonicalize(right));
    }
}
