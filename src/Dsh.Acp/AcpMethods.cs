namespace Dsh.Acp;

public static class AcpMethods
{
    public const int ProtocolVersion = 1;

    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";
    public const string NewSession = "session/new";
    public const string ListSessions = "session/list";
    public const string ResumeSession = "session/resume";
    public const string CloseSession = "session/close";
    public const string Prompt = "session/prompt";
    public const string Cancel = "session/cancel";

    public const string ClientSessionUpdate = "session/update";
    public const string ClientRequestPermission = "session/request_permission";

    public const string UpdateAgentMessageChunk = "agent_message_chunk";
    public const string UpdateAgentThoughtChunk = "agent_thought_chunk";
    public const string UpdateToolCall = "tool_call";
    public const string UpdateToolCallResult = "tool_call_update";
}