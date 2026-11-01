using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Boot;

/** Compares the logical settings location and actual file identity, including hardlink aliases. */
public static class SettingsFilePath
{
    public static bool SameFile(string left, string right)
    {
        if (PinnedFileAccess.Canonicalize(left) == PinnedFileAccess.Canonicalize(right))
            return true;
        if (!File.Exists(left) || !File.Exists(right))
            return false;
        using var first = File.OpenHandle(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = File.OpenHandle(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var firstIdentity = NativeFileIdentity.Get(first);
        var secondIdentity = NativeFileIdentity.Get(second);
        return firstIdentity.Device == secondIdentity.Device && firstIdentity.Inode == secondIdentity.Inode;
    }
}
