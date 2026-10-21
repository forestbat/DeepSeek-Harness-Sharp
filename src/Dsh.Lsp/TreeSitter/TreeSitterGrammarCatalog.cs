using Dsh.Lsp.Catalog;

namespace Dsh.Lsp.TreeSitter;

public static class TreeSitterGrammarCatalog
{
    public static IReadOnlyDictionary<string, string> GrammarNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["typescript"] = "typescript",
            ["javascript"] = "javascript",
            ["python"] = "python",
            ["java"] = "java",
            ["csharp"] = "c_sharp",
            ["cpp"] = "cpp",
            ["c"] = "c",
            ["go"] = "go",
            ["rust"] = "rust",
            ["php"] = "php",
            ["ruby"] = "ruby",
            ["kotlin"] = "kotlin",
            ["swift"] = "swift",
            ["scala"] = "scala",
            ["dart"] = "dart",
            ["lua"] = "lua",
            ["r"] = "r",
            ["sql"] = "sql",
            ["html"] = "html",
            ["css"] = "css",
            ["json"] = "json",
            ["yaml"] = "yaml",
            ["markdown"] = "markdown",
            ["xml"] = "xml",
            ["shellscript"] = "bash",
            ["powershell"] = "powershell",
            ["haskell"] = "haskell",
            ["elixir"] = "elixir",
            ["clojure"] = "clojure",
            ["julia"] = "julia",
        };

    public static IReadOnlyDictionary<string, string> Discover(string runtimePath)
    {
        var root = Directory.Exists(Path.Combine(runtimePath, "out"))
            ? Path.Combine(runtimePath, "out")
            : Path.Combine(runtimePath, "node_modules", "tree-sitter-wasm", "out");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in LanguageCatalog.All)
        {
            var grammar = GrammarNames.GetValueOrDefault(profile.Id);
            if (grammar is null)
                continue;
            var path = Path.Combine(root, grammar, $"tree-sitter-{grammar}.wasm");
            if (File.Exists(path))
                result[profile.Id] = path;
        }

        return result;
    }
}
