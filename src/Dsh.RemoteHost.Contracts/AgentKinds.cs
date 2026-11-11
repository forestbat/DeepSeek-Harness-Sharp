namespace Dsh.RemoteHost;

/** Stable identifiers of agent adapters; they travel in session records, so never rename them. */
public static class AgentKinds
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string OpenCode = "opencode";
    public const string KiloCode = "kilocode";

    /** Any agent that speaks ACP over stdio, launched with a user-supplied command. */
    public const string Acp = "acp";

    /** Unknown agent: a user-supplied command run as a raw terminal session. */
    public const string Terminal = "terminal";
}

