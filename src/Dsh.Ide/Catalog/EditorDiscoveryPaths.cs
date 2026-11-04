using Dsh.Core;
using Dsh.Ide.Selection;

namespace Dsh.Ide.Catalog;

internal static class EditorDiscoveryPaths
{
    internal static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    internal static Dictionary<string, string[]> Read(object? value)
        => ConfigMap.Read(value).ToDictionary(pair => pair.Key, pair => ConfigMap.Strings(pair.Value), StringComparer.Ordinal);

    internal static string Render(string value, EditorDescriptor? editor = null, string? home = null)
    {
        if (value.Contains("{home}", StringComparison.Ordinal)) value = value.Replace("{home}", home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
        if (value.Contains("{app_data}", StringComparison.Ordinal)) value = value.Replace("{app_data}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.Ordinal);
        if (value.Contains("{local_app_data}", StringComparison.Ordinal)) value = value.Replace("{local_app_data}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.Ordinal);
        if (value.Contains("{program_files}", StringComparison.Ordinal)) value = value.Replace("{program_files}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.Ordinal);
        if (value.Contains("{program_files_x86}", StringComparison.Ordinal)) value = value.Replace("{program_files_x86}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.Ordinal);
        return value.Replace("{id}", editor?.Id.Value ?? "", StringComparison.Ordinal)
            .Replace("{display_name}", editor?.DisplayName ?? "", StringComparison.Ordinal)
            .Replace("{command}", editor?.Command ?? "", StringComparison.Ordinal);
    }

    internal static IEnumerable<string> Directories(string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            if (Directory.Exists(pattern)) yield return pattern;
            yield break;
        }
        var root = Path.GetPathRoot(pattern) ?? throw new ArgumentException("Editor discovery paths must be absolute.");
        var parts = pattern[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in Descend(root, parts, 0)) yield return directory;
    }

    private static IEnumerable<string> Descend(string parent, string[] parts, int index)
    {
        if (index == parts.Length)
        {
            if (Directory.Exists(parent)) yield return parent;
            yield break;
        }
        if (parts[index].IndexOfAny(['*', '?']) < 0)
        {
            var child = Path.Combine(parent, parts[index]);
            if (Directory.Exists(child))
                foreach (var directory in Descend(child, parts, index + 1)) yield return directory;
            yield break;
        }
        if (!Directory.Exists(parent)) yield break;
        foreach (var child in Directory.EnumerateDirectories(parent).OrderDescending())
            if (EditorSelection.Glob(parts[index], Path.GetFileName(child)))
                foreach (var directory in Descend(child, parts, index + 1)) yield return directory;
    }
}
