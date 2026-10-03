namespace Dsh.A2A;

public sealed record A2aSkillOptions(
    string Id = "coding",
    string Name = "Coding",
    string Description = "General software engineering assistance",
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> EffectiveTags => Tags ?? ["coding", "assistant"];
}

public sealed record A2aServerOptions(
    string Host = "127.0.0.1",
    int Port = 0,
    string? PublicUrl = null,
    string? AuthToken = null)
{
    public A2aSkillOptions Skill { get; init; } = new();
}

public static class A2aAgentCardBuilder
{
    public const string ProtocolVersion = "1.0";
    public const string AgentName = "deepseek-harness";
    public const string Transport = "JSONRPC";

    public static A2aAgentCard Build(string endpoint, A2aServerOptions options)
    {
        var url = options.PublicUrl ?? endpoint;
        var version = typeof(A2aAgentCardBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.1";
        var secured = options.AuthToken is { Length: > 0 };
        return new A2aAgentCard(
            ProtocolVersion,
            AgentName,
            "DeepSeek Harness agent exposed over the A2A protocol",
            url,
            version,
            Transport,
            new A2aAgentCapabilities(Streaming: true, PushNotifications: false),
            ["text/plain"],
            ["text/plain"],
            [new A2aAgentSkill(options.Skill.Id, options.Skill.Name, options.Skill.Description, options.Skill.EffectiveTags)],
            secured
                ? new Dictionary<string, A2aHttpSecurityScheme> { ["bearer"] = new A2aHttpSecurityScheme("http", "bearer") }
                : null,
            secured
                ? [new Dictionary<string, IReadOnlyList<string>> { ["bearer"] = [] }]
                : null);
    }
}
