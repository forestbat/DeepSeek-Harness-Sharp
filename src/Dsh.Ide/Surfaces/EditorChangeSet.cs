using System.Text.Json;
using Dsh.Core;

namespace Dsh.Ide.Surfaces;

public static class EditorChangeSet
{
    public static IReadOnlyList<string> PreviousTurnFiles(Session session)
    {
        var turn = LastCompletedTurn(session);
        if (turn is null)
            return [];
        var cwd = session.Header.Cwd ?? Environment.CurrentDirectory;
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int? activeTurn = null;
        foreach (var item in session.SnapshotEvents())
        {
            if (item.Data is TurnStartPayload start)
                activeTurn = start.Turn;
            if (item.Data is WorkingDirectoryChangedPayload change)
                cwd = change.Cwd;
            if (item.Data is FileChangedPayload file && activeTurn == turn)
                paths.Add(file.Path);
            if (item.Data is ToolResultPayload result && result.Turn == turn && result.Error is null && result.Message.Block.IsError != true)
                AddChangedPaths(paths, cwd, result.Meta);
            if (item.Data is TurnEndPayload end)
            {
                if (end.Turn == turn)
                    break;
                activeTurn = null;
            }
        }
        return paths.ToArray();
    }

    public static long PreviousTurnStart(Session session)
    {
        var turn = LastCompletedTurn(session);
        for (var seq = session.Seq - 1; seq >= 0; seq--)
            if (session.EventAt(seq)?.Data is TurnStartPayload start && start.Turn == turn)
                return seq;
        return session.Seq;
    }

    private static int? LastCompletedTurn(Session session)
    {
        for (var seq = session.Seq - 1; seq >= 0; seq--)
            if (session.EventAt(seq)?.Data is TurnEndPayload end)
                return end.Turn;
        return null;
    }

    private static void AddChangedPaths(HashSet<string> paths, string cwd, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta || !meta.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.String || card.GetString() != "diff")
            return;
        if (!meta.TryGetProperty("diffs", out var diffs) || diffs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var diff in diffs.EnumerateArray())
        {
            if (diff.ValueKind == JsonValueKind.Object && diff.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } text)
                paths.Add(Path.GetFullPath(text, cwd));
        }
    }
}
