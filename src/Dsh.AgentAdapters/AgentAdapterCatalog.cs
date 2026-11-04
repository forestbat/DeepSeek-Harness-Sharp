namespace Dsh.AgentAdapters;

/**
 * Built-in adapters plus factories for user-defined agents.
 * Codex, Claude Code and OpenCode command lines were checked on 2026-10-15 against the installed CLI (`--help`)
 * and these vendor sources:
 * https://learn.chatgpt.com/docs/developer-commands?surface=cli, https://code.claude.com/docs/en/cli-reference,
 * https://opencode.ai/docs/cli/, https://opencode.ai/docs/acp/, https://github.com/agentclientprotocol (codex-acp, claude-agent-acp).
 */
public static class AgentAdapterCatalog
{
    /** npm runner used for ACP bridges that ship as npm packages (resolved to npx.cmd on Windows by AgentExecutableLocator). */
    public const string NpxExecutable = "npx";

    /** ACP bridge for Codex (Codex has no native ACP mode); replaces the deprecated @zed-industries/codex-acp. */
    public const string CodexAcpPackage = "@agentclientprotocol/codex-acp";

    /** ACP bridge for Claude Code (no native ACP mode); replaces the deprecated @zed-industries/claude-code-acp. */
    public const string ClaudeAcpPackage = "@agentclientprotocol/claude-agent-acp";

    /** Codex CLI 0.162: `codex [PROMPT]`, `codex resume <id>|--last`; the TUI uses the process working directory (`-C` is optional). */
    public static AgentAdapter Codex { get; } = new(
        AgentKinds.Codex,
        "Codex",
        new AgentCommand("codex", []),
        new AgentCommand(NpxExecutable, ["-y", CodexAcpPackage]),
        new AgentLaunchSyntax(
            Prompt: [AgentLaunchSyntax.PromptToken],
            ResumeById: ["resume", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["resume", "--last"]));

    /** Claude Code 2.1: `claude [prompt]`, `claude --resume <id>`, `claude --continue`; there is no top-level working directory flag. */
    public static AgentAdapter ClaudeCode { get; } = new(
        AgentKinds.ClaudeCode,
        "Claude Code",
        new AgentCommand("claude", []),
        new AgentCommand(NpxExecutable, ["-y", ClaudeAcpPackage]),
        new AgentLaunchSyntax(
            Prompt: [AgentLaunchSyntax.PromptToken],
            ResumeById: ["--resume", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["--continue"]));

    /** OpenCode 1.18: `opencode [project]`, `--prompt`, `--session <id>`, `--continue`; native ACP via `opencode acp`. */
    public static AgentAdapter OpenCode { get; } = new(
        AgentKinds.OpenCode,
        "OpenCode",
        new AgentCommand("opencode", []),
        new AgentCommand("opencode", ["acp"]),
        new AgentLaunchSyntax(
            Prompt: ["--prompt", AgentLaunchSyntax.PromptToken],
            ResumeById: ["--session", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["--continue"]));

    /** Kilo Code 7.8: native TUI and ACP, with --prompt, --session and --continue (https://kilo.ai/docs/code-with-ai/platforms/cli-reference). */
    public static AgentAdapter KiloCode { get; } = new(
        AgentKinds.KiloCode,
        "Kilo Code",
        new AgentCommand("kilo", []),
        new AgentCommand("kilo", ["acp"]),
        new AgentLaunchSyntax(
            Prompt: ["--prompt", AgentLaunchSyntax.PromptToken],
            ResumeById: ["--session", AgentLaunchSyntax.SessionIdToken],
            ResumeLatest: ["--continue"]));

    public static IReadOnlyList<AgentAdapter> BuiltIn { get; } = [Codex, ClaudeCode, OpenCode, KiloCode];

    public static AgentAdapter? Find(string kind) => BuiltIn.FirstOrDefault(adapter => adapter.Kind == kind);

    /** Unknown agent: any command run as a raw terminal session. */
    public static AgentAdapter Terminal(string displayName, AgentCommand command)
        => new(AgentKinds.Terminal, displayName, command, null, AgentLaunchSyntax.None);

    /** Any other ACP agent, driven only through its stdio ACP command. */
    public static AgentAdapter GenericAcp(string displayName, AgentCommand acpCommand)
        => new(AgentKinds.Acp, displayName, null, acpCommand, AgentLaunchSyntax.None);
}
