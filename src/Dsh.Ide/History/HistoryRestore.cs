using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Transport;

namespace Dsh.Ide.History;

/** Shared by interactive surfaces: preview first, snapshot-check under one exclusive handle, back up, then write. */
public static class HistoryRestore
{
    private const int MaxRestoreBytes = 16 * 1024 * 1024;
    private const int MaxPreviewLines = 200;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static IdeRestorePreview Preview(string workspaceRoot, IdeHistoryEntry entry, IdeHistoryContent content)
    {
        if (!IsWithinWorkspace(workspaceRoot, entry.Path))
            throw new InvalidOperationException("History target is outside the workspace.");
        if (content.Bytes.Length > MaxRestoreBytes)
            throw new InvalidOperationException("History revision exceeds the restore size limit.");
        var path = ResolvePath(entry.Path);
        var exists = File.Exists(path);
        var current = exists ? ReadCurrent(path) : [];
        var same = current.AsSpan().SequenceEqual(content.Bytes);
        var changed = !exists || !same && new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() > entry.Timestamp;
        return new IdeRestorePreview(Path.GetFullPath(workspaceRoot), entry with { Path = path }, content,
            exists, Digest(current), changed, Diff(current, content.Bytes));
    }

    public static IdeRestoreResult Restore(IdeRestorePreview preview, string backupDirectory, bool confirmModified)
    {
        if (preview.ModifiedSinceEntry && !confirmModified)
            return new(false, Failure: "The file changed after this history entry; explicit confirmation is required.");
        string? backup = null;
        try
        {
            if (!IsWithinWorkspace(preview.WorkspaceRoot, preview.Entry.Path)
                || !string.Equals(ResolvePath(preview.Entry.Path), preview.Entry.Path, PathComparison))
                return new(false, Failure: "The history target path changed or leaves the workspace.");
            var path = preview.Entry.Path;
            if (Path.GetDirectoryName(path) is { } parent)
                Directory.CreateDirectory(parent);
            if (!IsWithinWorkspace(preview.WorkspaceRoot, path))
                return new(false, Failure: "The history target leaves the workspace.");
            if (!preview.TargetExists)
                backup = RecordBackup(preview, backupDirectory, []);
            using var target = new FileStream(path, preview.TargetExists ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            var current = ReadCurrent(target);
            if (Digest(current) != preview.CurrentDigest)
                return new(false, Failure: "The file changed since preview; request a new preview before restoring.");
            backup ??= RecordBackup(preview, backupDirectory, current);
            target.Position = 0;
            target.Write(preview.Content.Bytes);
            target.SetLength(preview.Content.Bytes.Length);
            target.Flush(flushToDisk: true);
            return new(true, backup);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, backup, "Restore could not acquire or write the target; any recorded backup has been retained.");
        }
    }

    private static string RecordBackup(IdeRestorePreview preview, string directory, ReadOnlySpan<byte> current)
    {
        OwnerOnlyFiles.EnsureDirectory(directory);
        var backup = Path.Combine(directory, $"{Guid.NewGuid():N}.original");
        OwnerOnlyFiles.Write(backup, current);
        OwnerOnlyFiles.Write($"{backup}.json", new JsonObject
        {
            ["path"] = preview.Entry.Path,
            ["existed"] = preview.TargetExists,
            ["historyTimestamp"] = preview.Entry.Timestamp,
            ["backupTimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }.ToJsonString());
        return backup;
    }

    public static bool IsWithinWorkspace(string root, string path)
    {
        try
        {
            var resolvedRoot = Path.TrimEndingDirectorySeparator(ResolvePath(root));
            var resolvedPath = ResolvePath(path);
            var prefix = Path.EndsInDirectorySeparator(resolvedRoot) ? resolvedRoot : $"{resolvedRoot}{Path.DirectorySeparatorChar}";
            return resolvedPath.StartsWith(prefix, PathComparison);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("Cannot resolve history target link.");
        }
        return Path.GetFullPath(current);
    }

    private static byte[] ReadCurrent(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCurrent(stream);
    }

    private static byte[] ReadCurrent(FileStream stream)
    {
        if (stream.Length > MaxRestoreBytes)
            throw new IOException("Current file exceeds the restore size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Diff(byte[] current, byte[] revision)
    {
        if (current.AsSpan().SequenceEqual(revision))
            return "No content changes.";
        if (current.Contains((byte)0) || revision.Contains((byte)0))
            return $"Binary revision: {current.Length} bytes -> {revision.Length} bytes.";
        var before = Encoding.UTF8.GetString(current).ReplaceLineEndings("\n").Split('\n');
        var after = Encoding.UTF8.GetString(revision).ReplaceLineEndings("\n").Split('\n');
        var builder = new StringBuilder($"--- working tree\n+++ history revision\n@@ -1,{before.Length} +1,{after.Length} @@\n");
        foreach (var line in before.Take(MaxPreviewLines / 2))
            builder.Append('-').AppendLine(line);
        foreach (var line in after.Take(MaxPreviewLines / 2))
            builder.Append('+').AppendLine(line);
        if (before.Length > MaxPreviewLines / 2 || after.Length > MaxPreviewLines / 2)
            builder.AppendLine("[Preview truncated; restore applies the complete revision.]");
        return builder.ToString();
    }
}
