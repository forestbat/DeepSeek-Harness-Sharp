using Dsh.Lsp.Client;

namespace Dsh.Lsp.Catalog;

public sealed record LanguageServerProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> LanguageIds,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<LspServerLaunch> Servers,
    bool OfficialServer);

/** Curated profiles for the thirty mainstream languages with a usable LSP endpoint. */
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageServerProfile> All { get; } =
    [
        Profile(
            "typescript",
            "TypeScript",
            ["typescript", "typescriptreact"],
            [".ts", ".tsx", ".mts", ".cts"],
            [],
            true,
            new LspServerLaunch("tsgo", ["--lsp", "--stdio"],
                "Install the official TypeScript native preview with 'npm install -g @typescript/native-preview'.")),

        Profile(
            "javascript",
            "JavaScript",
            ["javascript", "javascriptreact"],
            [".js", ".jsx", ".mjs", ".cjs"],
            [],
            true,
            new LspServerLaunch("tsgo", ["--lsp", "--stdio"],
                "Install the official TypeScript native preview with 'npm install -g @typescript/native-preview'.")),

        Profile(
            "python",
            "Python",
            ["python"],
            [".py", ".pyi"],
            [],
            false,
            new LspServerLaunch("pyright-langserver", ["--stdio"],
                "Install Pyright with 'npm install -g pyright'.")),

        Profile(
            "java",
            "Java",
            ["java"],
            [".java"],
            [],
            true,
            new LspServerLaunch("jdtls", [],
                "Install Eclipse JDT Language Server and put 'jdtls' on PATH.")),

        Profile(
            "csharp",
            "C#",
            ["csharp"],
            [".cs", ".csx"],
            [],
            true,
            new LspServerLaunch("roslyn-language-server",
                ["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
                "Install the official server with 'dotnet tool install --global roslyn-language-server --prerelease'.")),

        Profile(
            "cpp",
            "C++",
            ["cpp", "c++"],
            [".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx"],
            [],
            true,
            new LspServerLaunch("clangd", [],
                "Install clangd from the LLVM release for your platform.")),

        Profile(
            "c",
            "C",
            ["c"],
            [".c", ".h"],
            [],
            true,
            new LspServerLaunch("clangd", [],
                "Install clangd from the LLVM release for your platform.")),

        Profile(
            "go",
            "Go",
            ["go"],
            [".go"],
            [],
            true,
            new LspServerLaunch("gopls", [],
                "Install the official server with 'go install golang.org/x/tools/gopls@latest'.")),

        Profile(
            "rust",
            "Rust",
            ["rust"],
            [".rs"],
            [],
            true,
            new LspServerLaunch("rust-analyzer", [],
                "Install the official server with 'rustup component add rust-analyzer'.")),

        Profile(
            "php",
            "PHP",
            ["php"],
            [".php"],
            [],
            false,
            new LspServerLaunch("intelephense", ["--stdio"],
                "Install Intelephense with 'npm install -g intelephense'.")),

        Profile(
            "ruby",
            "Ruby",
            ["ruby"],
            [".rb", ".rake", ".gemspec"],
            ["Gemfile", "Rakefile"],
            false,
            new LspServerLaunch("ruby-lsp", [],
                "Install the Ruby LSP with 'gem install ruby-lsp'.")),

        Profile(
            "kotlin",
            "Kotlin",
            ["kotlin"],
            [".kt", ".kts"],
            [],
            true,
            new LspServerLaunch("kotlin-lsp", [],
                "Install the official Kotlin language server and put 'kotlin-lsp' on PATH.")),

        Profile(
            "swift",
            "Swift",
            ["swift"],
            [".swift"],
            [],
            true,
            new LspServerLaunch("sourcekit-lsp", [],
                "Install the Swift toolchain; sourcekit-lsp ships with it.")),

        Profile(
            "scala",
            "Scala",
            ["scala"],
            [".scala", ".sc"],
            [],
            true,
            new LspServerLaunch("metals", [],
                "Install Metals with Coursier and put 'metals' on PATH.")),

        Profile(
            "dart",
            "Dart",
            ["dart"],
            [".dart"],
            [],
            true,
            new LspServerLaunch("dart", ["language-server"],
                "Install the Dart SDK; the language server ships with it.")),

        Profile(
            "lua",
            "Lua",
            ["lua"],
            [".lua"],
            [],
            false,
            new LspServerLaunch("lua-language-server", [],
                "Install LuaLS and put 'lua-language-server' on PATH.")),

        Profile(
            "r",
            "R",
            ["r"],
            [".r", ".rmd"],
            [],
            false,
            new LspServerLaunch("Rscript", ["--no-save", "--no-restore", "-e", "languageserver::run()"],
                "Install the R languageserver package with 'install.packages(\"languageserver\")'.")),

        Profile(
            "sql",
            "SQL",
            ["sql"],
            [".sql"],
            [],
            false,
            new LspServerLaunch("sqls", [],
                "Install sqls with 'go install github.com/lighttiger2505/sqls@latest'.")),

        Profile(
            "html",
            "HTML",
            ["html"],
            [".html", ".htm"],
            [],
            true,
            new LspServerLaunch("vscode-html-language-server", ["--stdio"],
                "Install vscode-langservers-extracted with 'npm install -g vscode-langservers-extracted'.")),

        Profile(
            "css",
            "CSS",
            ["css", "scss", "less"],
            [".css", ".scss", ".sass", ".less"],
            [],
            true,
            new LspServerLaunch("vscode-css-language-server", ["--stdio"],
                "Install vscode-langservers-extracted with 'npm install -g vscode-langservers-extracted'.")),

        Profile(
            "json",
            "JSON",
            ["json", "jsonc"],
            [".json", ".jsonc"],
            [],
            true,
            new LspServerLaunch("vscode-json-language-server", ["--stdio"],
                "Install vscode-langservers-extracted with 'npm install -g vscode-langservers-extracted'.")),

        Profile(
            "yaml",
            "YAML",
            ["yaml"],
            [".yaml", ".yml"],
            [],
            false,
            new LspServerLaunch("yaml-language-server", ["--stdio"],
                "Install the YAML language server with 'npm install -g yaml-language-server'.")),

        Profile(
            "markdown",
            "Markdown",
            ["markdown"],
            [".md", ".markdown"],
            [],
            false,
            new LspServerLaunch("marksman", ["server"],
                "Install Marksman and put 'marksman' on PATH.")),

        Profile(
            "xml",
            "XML",
            ["xml"],
            [".xml", ".xsd", ".xsl", ".xslt", ".svg"],
            [],
            true,
            new LspServerLaunch("lemminx", [],
                "Install Eclipse LemMinX and put 'lemminx' on PATH.")),

        Profile(
            "shellscript",
            "Shell",
            ["shellscript", "bash", "sh"],
            [".sh", ".bash", ".zsh", ".ksh"],
            [],
            false,
            new LspServerLaunch("bash-language-server", ["start"],
                "Install the shell language server with 'npm install -g bash-language-server'.")),

        Profile(
            "powershell",
            "PowerShell",
            ["powershell"],
            [".ps1", ".psm1", ".psd1", ".ps1xml"],
            [],
            true,
            new LspServerLaunch("pwsh", ["-NoLogo", "-NoProfile", "-Command", "Start-EditorServices -Stdio"],
                "Install the PowerShell Editor Services module and make Start-EditorServices available in pwsh.")),

        Profile(
            "haskell",
            "Haskell",
            ["haskell"],
            [".hs", ".lhs"],
            [],
            true,
            new LspServerLaunch("haskell-language-server-wrapper", ["--lsp"],
                "Install GHC and haskell-language-server through ghcup.")),

        Profile(
            "elixir",
            "Elixir",
            ["elixir"],
            [".ex", ".exs"],
            [],
            false,
            new LspServerLaunch("language_server.sh", [],
                "Install ElixirLS and put its language_server.sh on PATH.")),

        Profile(
            "clojure",
            "Clojure",
            ["clojure"],
            [".clj", ".cljs", ".cljc", ".edn"],
            [],
            false,
            new LspServerLaunch("clojure-lsp", [],
                "Install clojure-lsp and put the launcher on PATH.")),

        Profile(
            "julia",
            "Julia",
            ["julia"],
            [".jl"],
            [],
            false,
            new LspServerLaunch("julia",
                ["--startup-file=no", "--history-file=no", "-e", "using LanguageServer; runserver()"],
                "Install LanguageServer.jl in the active Julia environment.")),
    ];

    public static LanguageServerProfile? FindById(string id)
        => All.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static LanguageServerProfile? FindByLanguageId(string languageId)
        => All.FirstOrDefault(profile => profile.LanguageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase));

    public static LanguageServerProfile? FindByPath(string path)
    {
        var name = Path.GetFileName(path);
        var exact = All.FirstOrDefault(profile => profile.FileNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var extension = Path.GetExtension(path);
        return All
            .Where(profile => profile.Extensions.Any(candidate => candidate.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(profile => profile.Extensions.Max(candidate => candidate.Length))
            .FirstOrDefault();
    }

    public static LanguageServerProfile ResolveByPath(string path)
        => FindByPath(path) ?? throw new NotSupportedException($"no language profile for '{path}'");

    private static LanguageServerProfile Profile(
        string id,
        string displayName,
        IReadOnlyList<string> languageIds,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string> fileNames,
        bool officialServer,
        params LspServerLaunch[] servers)
        => new(id, displayName, languageIds, extensions, fileNames, servers, officialServer);
}
