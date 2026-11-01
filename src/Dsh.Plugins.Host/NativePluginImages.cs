using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dsh.Plugins.Native;

namespace Dsh.Plugins;

/** NativeAOT runtime threads and signal handlers outlive logical plugin deactivation. */
internal static class NativePluginImages
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, Image> Images = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static nint Load(string path)
    {
        lock (Sync)
        {
            using var source = File.OpenRead(path);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(source, digest);
            if (Images.TryGetValue(path, out var image))
            {
                if (!digest.SequenceEqual(image.Digest))
                    throw new InvalidOperationException("A resident native plugin image changed; use a new worker or restart the host.");
                return image.Handle;
            }
            DshNativeGuard.Install();
            var handle = NativeLibrary.Load(path);
            Images.Add(path, new Image(handle, digest.ToArray()));
            return handle;
        }
    }

    private readonly record struct Image(nint Handle, byte[] Digest);
}
