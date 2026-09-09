namespace Cordis.Loader;

public sealed class IncludeStaleWriteException : Exception
{
    public IncludeStaleWriteException()
        : base("config file changed before the atomic write could complete")
    {
    }
}

public static class IncludeFileWriter
{
    public static async Task WriteAtomicAsync(string path, string content, string? expectedContent = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content);
            if (expectedContent is not null)
            {
                var current = await File.ReadAllTextAsync(fullPath);
                if (current != expectedContent) throw new IncludeStaleWriteException();
            }
            File.Move(tempPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
