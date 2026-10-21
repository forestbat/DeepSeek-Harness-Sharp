namespace Dsh.Lsp.TreeSitter;

public sealed record TreeSitterOptions(
    bool Enabled = false,
    string NodePath = "node",
    string? RuntimePath = null,
    IReadOnlyDictionary<string, string>? GrammarPaths = null,
    IReadOnlySet<string>? AllowedLanguages = null,
    int MaxFileBytes = 2 * 1024 * 1024,
    TimeSpan? Timeout = null)
{
    public bool Allows(string languageId)
        => Enabled
            && GrammarPaths is { Count: > 0 }
            && (AllowedLanguages is null
                || AllowedLanguages.Count == 0
                || AllowedLanguages.Contains(languageId));
}
