namespace Dsh.Skills;

public static class SkillSources
{
    public const string ProjectDsh = "project-dsh";
    public const string ProjectAgents = "project-agents";
    public const string Runtime = "runtime";
    public const string UserDsh = "user-dsh";
    public const string UserAgents = "user-agents";
    public const string Custom = "custom";
    public const string Bundled = "bundled";
}

public readonly record struct SkillInvocationPolicy(bool ModelInvocable, bool UserInvocable);

public abstract record SkillResourceBase
{
    public sealed record Directory(string Path) : SkillResourceBase;

    public sealed record UrlResource : SkillResourceBase
    {
        public required string Url { get; init; }
    }

    public sealed record Opaque(string Description) : SkillResourceBase;
}

public record SkillSummary
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? WhenToUse { get; init; }
    public required SkillInvocationPolicy Invocation { get; init; }
    public required string Source { get; init; }
    public required string Provider { get; init; }
    public SkillResourceBase? ResourceBase { get; init; }
}

public sealed record SkillCandidate : SkillSummary
{
    public int Rank { get; init; }
    public object? Locator { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public sealed record SkillDefinition : SkillSummary
{
    public required string Content { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public sealed record SkillRegistration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? WhenToUse { get; init; }
    public SkillInvocationPolicy? Invocation { get; init; }
    public required string Source { get; init; }
    public string? Provider { get; init; }
    public SkillResourceBase? ResourceBase { get; init; }
    public required string Content { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public record SkillLookupOptions
{
    public string? Cwd { get; init; }
    public CancellationToken Signal { get; init; }
}

public sealed record SkillViewOptions : SkillLookupOptions
{
    public Core.ScopeKey? Scope { get; init; }
}

public sealed record SkillCatalogSnapshot(IReadOnlyList<SkillSummary> Skills, bool Complete);

public sealed record SkillProviderObservation(IReadOnlyList<SkillCandidate> Candidates, bool Complete)
{
    public static SkillProviderObservation Full(IReadOnlyList<SkillCandidate> candidates) => new(candidates, true);
}

public interface ISkillProvider
{
    string Name { get; }

    Task<SkillProviderObservation> List(SkillLookupOptions options);

    Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options);
}

public sealed class SkillProviderControl
{
    private readonly Action _invalidate;

    internal SkillProviderControl(CancellationToken signal, Action invalidate)
    {
        Signal = signal;
        _invalidate = invalidate;
    }

    public CancellationToken Signal { get; }

    public void Invalidate() => _invalidate();
}

public static class SkillRender
{
    public static string RenderSkillContent(string name, string provider, SkillResourceBase? resourceBase, string content)
    {
        var resourceHint = RenderResourceHint(provider, resourceBase);
        return string.Join('\n',
        [
            $"<skill_content name=\"{EscapeAttr(name)}\">",
            "<skill_resources>",
            ..resourceHint,
            "</skill_resources>",
            "",
            "<skill_instructions>",
            content,
            "</skill_instructions>",
            "</skill_content>",
        ]);
    }

    private static IReadOnlyList<string> RenderResourceHint(string provider, SkillResourceBase? resourceBase)
        => resourceBase switch
        {
            null =>
            [
                $"Resources for this skill are managed by provider \"{EscapeText(provider)}\".",
                "Load referenced resources only as needed.",
            ],
            SkillResourceBase.Directory directory =>
            [
                $"Base directory for this skill: {EscapeText(directory.Path)}",
                "Resolve relative paths mentioned by this skill against the base directory before using them. Load referenced resources only as needed.",
            ],
            SkillResourceBase.UrlResource url =>
            [
                $"Base URL for this skill: {EscapeText(url.Url)}",
                "Resolve relative URLs mentioned by this skill against the base URL before using them. Load referenced resources only as needed.",
            ],
            SkillResourceBase.Opaque opaque =>
            [
                $"Resources for this skill: {EscapeText(opaque.Description)}",
                "Load referenced resources only as needed.",
            ],
            _ => throw new InvalidOperationException("unknown SkillResourceBase kind"),
        };

    private static string EscapeAttr(string value)
        => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    public static string EscapeText(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
