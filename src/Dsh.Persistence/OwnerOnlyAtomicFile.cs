using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.Persistence;

internal static class OwnerOnlyAtomicFile
{
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(5);
    private const int LockAttempts = 400;

    public static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, OwnerDirectoryMode);
        File.SetUnixFileMode(directory, OwnerDirectoryMode);
    }

    public static FileStream AcquireLock(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"invalid target path \"{path}\"");
        EnsureDirectory(directory);
        var lockPath = path + ".lock";
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerFileMode;
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException) when (attempt + 1 < LockAttempts)
            {
                Thread.Sleep(LockRetryDelay);
            }
        }
        throw new IOException($"timed out acquiring file lock \"{lockPath}\"");
    }

    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"invalid target path \"{path}\"");
        EnsureDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerFileMode;
            using (var stream = new FileStream(tempPath, options))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
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

    public static T? ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path))
            return default;
        var bytes = File.ReadAllBytes(path);
        return bytes.Length == 0 ? default : JsonSerializer.Deserialize(bytes, typeInfo);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
