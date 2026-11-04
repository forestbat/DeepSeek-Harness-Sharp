using Dsh.Core;
using Dsh.Ide.Selection;

namespace Dsh.Ide.History;

/// <summary>Enables configured history formats only when an editor's workspace conditions match within the project boundary.</summary>
public static class WorkspaceIdeDetector
{
    // Bound the parent walk independently of marker configuration.
    public const int MaxDepth = 24;

    public static IdeWorkspaceKind Detect(string? workspaceRoot, IReadOnlyList<EditorDescriptor> descriptors)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return IdeWorkspaceKind.None;
        var configuredKinds = IdeWorkspaceKind.None;
        foreach (var descriptor in descriptors)
            if (descriptor.When.Workspace.Length > 0) configuredKinds |= descriptor.HistoryProvider;
        return configuredKinds == IdeWorkspaceKind.None ? configuredKinds : Detect(new DirectoryInfo(Path.GetFullPath(workspaceRoot)), descriptors, configuredKinds);
    }

    private static IdeWorkspaceKind Detect(DirectoryInfo? directory, IReadOnlyList<EditorDescriptor> descriptors, IdeWorkspaceKind configuredKinds)
    {
        var kind = IdeWorkspaceKind.None;
        for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
        {
            kind = DetectAt(directory, descriptors, kind, configuredKinds);
            // A repository file or directory terminates inheritance from unrelated parents.
            if (kind == configuredKinds || Path.Exists(Path.Combine(directory.FullName, ".git")))
                break;
            directory = directory.Parent;
        }
        return kind;
    }

    private static IdeWorkspaceKind DetectAt(DirectoryInfo directory, IReadOnlyList<EditorDescriptor> descriptors, IdeWorkspaceKind kind, IdeWorkspaceKind configuredKinds)
    {
        // Missing working directories can still inherit matching parent markers.
        if (!directory.Exists) return kind;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory.FullName))
        {
            var marker = Path.GetFileName(entry);
            foreach (var descriptor in descriptors)
                if (descriptor.HistoryProvider != IdeWorkspaceKind.None && !kind.HasFlag(descriptor.HistoryProvider)
                    && EditorSelection.MatchesWorkspaceMarker(descriptor.When, marker))
                    kind |= descriptor.HistoryProvider;
            if (kind == configuredKinds) return kind;
        }
        return kind;
    }
}
