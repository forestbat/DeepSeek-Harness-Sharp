namespace Dsh.A2A;

public sealed record A2aSkillOptions(
    string Id = "coding",
    string Name = "Coding",
    string Description = "General software engineering assistance",
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> EffectiveTags => Tags ?? ["coding", "assistant"];
}

public sealed record A2aHostOptions(
    string Host = "127.0.0.1",
    int Port = 0,
    string? PublicUrl = null,
    string? AuthToken = null)
{
    public A2aSkillOptions Skill { get; init; } = new();
}
