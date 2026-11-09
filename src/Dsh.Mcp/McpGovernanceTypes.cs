namespace Dsh.Mcp;

public enum McpConflictKind
{
    NameCollision,
    SemanticRedundancy,
    HostToolShadowing,
    DefectiveDefinition,
}

public enum McpResolutionAction
{
    DisableTool,
    PreferServer,
    PrioritizeHostTool,
    ApplyPromptGuidance,
}

public enum McpResolutionStatus
{
    Pending,
    Approved,
    Rejected,
    Applied,
}

public sealed record McpPromptInfo(
    string Name,
    string? Description = null,
    IReadOnlyList<string>? Arguments = null);

public sealed record McpConflict(
    string ConflictId,
    McpConflictKind Kind,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ToolNames,
    string Description,
    McpResolutionAction SuggestedAction,
    string TargetServerOrTool);

public sealed record McpConflictResolution(
    string Id,
    McpConflictKind Kind,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ToolNames,
    string Description,
    McpResolutionAction Action,
    string Target,
    McpResolutionStatus Status,
    long CreatedAt,
    long? DecidedAt = null);

public sealed record McpGovernanceReport(
    IReadOnlyList<McpConflict> Conflicts,
    IReadOnlyList<McpConflictResolution> Resolutions,
    string TipsPath,
    long GeneratedAt);
