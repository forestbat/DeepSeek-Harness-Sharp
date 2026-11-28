using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Runtime;

namespace Dsh.Channels;

internal static class ChannelStorage
{
    public static string? ResolveChannelsDirectory(Context ctx, string? overrideDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);
        if (DshPaths.FromContext(ctx) is { } paths)
            return paths.Path("channels");
        return null;
    }

    public static T? ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path))
            return default;
        var bytes = File.ReadAllBytes(path);
        return bytes.Length == 0 ? default : JsonSerializer.Deserialize(bytes, typeInfo);
    }

    public static void WriteJsonAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            EnsureOwnerDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = OpenOwnerTempStream(tempPath, async: false))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            EnsureOwnerDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = OpenOwnerTempStream(tempPath, async: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void EnsureOwnerDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream OpenOwnerTempStream(string tempPath, bool async)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = async ? FileOptions.Asynchronous : FileOptions.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(tempPath, options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failure after write error.
        }
    }
}
