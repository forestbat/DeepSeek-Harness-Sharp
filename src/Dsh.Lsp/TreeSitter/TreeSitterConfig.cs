namespace Dsh.Lsp.TreeSitter;

public static class TreeSitterConfig
{
    public static ISyntaxTreeProvider? CreateProvider(object? config)
    {
        if (config is not IReadOnlyDictionary<string, object?> root)
            return null;
        var section = root.TryGetValue("treeSitter", out var nested)
            ? nested as IReadOnlyDictionary<string, object?> ?? root
            : root;
        if (!Bool(section, "enabled"))
            return null;
        var runtimePath = String(section, "runtimePath");
        if (string.IsNullOrWhiteSpace(runtimePath))
            return null;
        var grammars = section.TryGetValue("grammarPaths", out var grammarValue)
            && grammarValue is IReadOnlyDictionary<string, object?> grammarMap
                ? grammarMap
                    .Where(pair => pair.Value is string)
                    .ToDictionary(pair => pair.Key, pair => (string)pair.Value!, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, path) in TreeSitterGrammarCatalog.Discover(runtimePath))
            grammars.TryAdd(language, path);
        var allowed = section.TryGetValue("allowedLanguages", out var allowedValue)
            && allowedValue is IEnumerable<object?> items
                ? items.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
        var options = new TreeSitterOptions(
            true,
            String(section, "nodePath") ?? "node",
            runtimePath,
            grammars,
            allowed,
            Int(section, "maxFileBytes") ?? 2 * 1024 * 1024);
        return new TreeSitterSidecarProvider(options);
    }

    private static string? String(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? value as string : null;

    private static bool Bool(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            && (value is true || value is string text && bool.TryParse(text, out var parsed) && parsed);

    private static int? Int(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value)
            ? value switch
            {
                int number => number,
                long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => null,
            }
            : null;
}
