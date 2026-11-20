using Dsh.Llm;
using Dsh.Core;

namespace Dsh.Agent;

/** Kilo 风格的 user 尾部事实块: 只放短环境信息, 不放指令型 runtime context。 */
public static class EnvironmentDetails
{
    public const string OpenTag = "<environment_details>";
    public const string CloseTag = "</environment_details>";

    public static IReadOnlyList<Message> Append(Session session, IReadOnlyList<Message> messages)
    {
        var snapshots = UserSnapshots(session);
        var result = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            if (message is not UserMessage || message.Source is not UserMessageSource || HasDetails(message))
            {
                result.Add(message);
                continue;
            }
            var snapshot = snapshots.TryGetValue(message.Id.Value, out var value)
                ? value
                : new Snapshot(session.Header.CreatedAt, session.WorkingDirectory ?? session.Header.Cwd ?? "");
            result.Add(message with
            {
                Content = [.. message.Content, new TextBlock($"\n\n{Render(session, snapshot)}")],
            });
        }
        return result;
    }

    public static string Render(Session session, Snapshot snapshot)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.Time)
            .ToLocalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var workspaceRoot = session.Header.Cwd ?? snapshot.WorkingDirectory;
        return $"""
            {OpenTag}
            Message time: {timestamp}
            Working directory: {snapshot.WorkingDirectory}
            Workspace root folder: {workspaceRoot}
            {CloseTag}
            """;
    }

    public readonly record struct Snapshot(long Time, string WorkingDirectory);

    private static Dictionary<string, Snapshot> UserSnapshots(Session session)
    {
        var snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        // 窗口会话里只有尾巴上的消息会被追加事实块: 从常驻段起点折起, 不把前缀按页读回来。
        // 窗口内的第一条消息若之前还有一次工作目录变更(在窗口之前), 它拿到的是当前工作目录而不是当时那个。
        var workingDirectory = session.ResidentFrom > 0
            ? session.WorkingDirectory ?? session.Header.Cwd ?? ""
            : session.Header.Cwd ?? "";
        foreach (var sessionEvent in session.ResidentEvents())
        {
            if (sessionEvent.Data is WorkingDirectoryChangedPayload change)
                workingDirectory = change.Cwd;
            else if (sessionEvent.Data is UserMessagePayload payload)
                snapshots[payload.Message.Id.Value] = new Snapshot(sessionEvent.Time, workingDirectory);
        }
        return snapshots;
    }

    private static bool HasDetails(Message message)
        => message.Content.OfType<TextBlock>().Any(block => block.Text.Contains(OpenTag, StringComparison.Ordinal));
}
