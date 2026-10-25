using Dsh.AgentAdapters;
using Dsh.Fleet;
using Dsh.Notifications;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** How the badge of an agent kind looks: a short glyph and a fixed color, so the same agent is recognisable on every machine. */
public readonly record struct AgentBadge(string Glyph, string Color);

/** Display texts shared by the dashboard, the command palette, the session view and the notification center. */
public static class SessionPresentation
{
    /** Agent kinds the host does not describe (user-defined commands) keep at most this many characters as their badge. */
    private const int CustomBadgeLength = 2;

    private static readonly AgentBadge HarnessBadge = new("DS", "#4D6BFE");
    private static readonly AgentBadge CodexBadge = new("CX", "#10A37F");
    private static readonly AgentBadge ClaudeBadge = new("CC", "#D97757");
    private static readonly AgentBadge OpenCodeBadge = new("OC", "#8B5CF6");
    private static readonly AgentBadge AcpBadge = new("AC", "#0EA5E9");
    private static readonly AgentBadge TerminalBadge = new(">_", "#6B7280");
    private static readonly AgentBadge CustomBadgeColor = new("", "#9CA3AF");

    public static string AgentLabel(string? agentKind, string sessionKind) => agentKind switch
    {
        HostAgentKinds.Dsharp => "DeepSeek Harness",
        AgentKinds.Codex => "Codex",
        AgentKinds.ClaudeCode => "Claude Code",
        AgentKinds.OpenCode => "OpenCode",
        AgentKinds.Acp => "ACP Agent",
        AgentKinds.Terminal => "自定义命令",
        null when sessionKind == HostSessionKinds.Terminal => "终端",
        null => sessionKind,
        _ => agentKind,
    };

    public static AgentBadge Badge(string? agentKind, string sessionKind) => agentKind switch
    {
        HostAgentKinds.Dsharp => HarnessBadge,
        AgentKinds.Codex => CodexBadge,
        AgentKinds.ClaudeCode => ClaudeBadge,
        AgentKinds.OpenCode => OpenCodeBadge,
        AgentKinds.Acp => AcpBadge,
        AgentKinds.Terminal => TerminalBadge,
        null when sessionKind == HostSessionKinds.Terminal => TerminalBadge,
        null => CustomBadgeColor with { Glyph = Abbreviate(sessionKind) },
        _ => CustomBadgeColor with { Glyph = Abbreviate(agentKind) },
    };

    public static string StatusLabel(string status) => status switch
    {
        HostSessionStatuses.Running => "运行中",
        HostSessionStatuses.WaitingForUser => "等待你处理",
        HostSessionStatuses.Idle => "空闲",
        HostSessionStatuses.Exited => "已退出",
        _ => status,
    };

    public static string MachineStateLabel(MachineConnectionState state) => state switch
    {
        MachineConnectionState.Connecting => "连接中",
        MachineConnectionState.Connected => "已连接",
        MachineConnectionState.Reconnecting => "重连中",
        MachineConnectionState.Deploying => "部署中",
        _ => "离线",
    };

    public static string KindLabel(string sessionKind) => sessionKind switch
    {
        HostSessionKinds.Harness => "harness",
        HostSessionKinds.External => "终端模式",
        HostSessionKinds.Acp => "ACP",
        HostSessionKinds.Terminal => "终端",
        _ => sessionKind,
    };

    public static string NotificationKindLabel(NotificationKind kind) => kind switch
    {
        NotificationKind.WaitingForInput => "等待输入",
        NotificationKind.ApprovalRequest => "审批",
        NotificationKind.Question => "提问",
        NotificationKind.SessionExited => "会话退出",
        NotificationKind.Error => "错误",
        _ => "消息",
    };

    /** The last path segment of a project directory (POSIX or Windows separators), or the whole path when it has none. */
    public static string ProjectName(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return "未指定项目";
        var trimmed = projectPath.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 || cut == trimmed.Length - 1 ? projectPath : trimmed[(cut + 1)..];
    }

    /** Attention is backed by real unanswered requests, never terminal screen text. */
    public static bool NeedsAttention(RemoteSessionSummary session)
        => session.PendingCount > 0;

    private static string Abbreviate(string kind)
        => kind.Length <= CustomBadgeLength ? kind.ToUpperInvariant() : kind[..CustomBadgeLength].ToUpperInvariant();
}
