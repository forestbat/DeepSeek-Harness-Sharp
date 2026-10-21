using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Lsp.Catalog;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.TreeSitter;
using Dsh.Lsp.Workspace;
using Dsh.Runtime;

namespace Dsh.Lsp.Tools;

/** MCP-facing tools over the structured language-server layer. */
public static class LspToolSet
{
    public const string AnalyzeTool = "lsp_analyze";
    public const string QueryTool = "lsp_query";

    public static IDisposable Register(Context context, ISyntaxTreeProvider? syntaxTreeProvider = null)
    {
        var tools = context.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return new NoopDisposable();

        var workspaces = new ConcurrentDictionary<string, LspWorkspace>(StringComparer.OrdinalIgnoreCase);
        var registrations = new List<IDisposable>
        {
            tools.Register(new ToolDefinition
            {
                Name = AnalyzeTool,
                Description = "Return the symbol outline, diagnostics, scopes, types, symbol edges, and hierarchy for a source file through its language server.",
                Parameters = Parameters(),
                Output = Output(),
                Execute = (args, exec) => AnalyzeAsync(args, exec, workspaces, syntaxTreeProvider),
                IsConcurrencySafe = _ => true,
            }),
            tools.Register(new ToolDefinition
            {
                Name = QueryTool,
                Description = "Query a language server for definition, references, hover, diagnostics, highlights, rename edits, code actions, outline, or workspace symbols.",
                Parameters = Parameters(),
                Output = Output(),
                Execute = (args, exec) => QueryAsync(args, exec, workspaces, syntaxTreeProvider),
                IsConcurrencySafe = _ => true,
            }),
        };
        return new Registration(registrations, workspaces);
    }

    private static async Task<object?> AnalyzeAsync(
        JsonElement args,
        ToolRunContext exec,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        ISyntaxTreeProvider? syntaxTreeProvider)
    {
        var (workspace, path) = Resolve(args, exec, workspaces, syntaxTreeProvider);
        var text = LspJson.String(args, "text");
        var analysis = await workspace.AnalyzeAndIndexFileAsync(path, text, exec.Signal).ConfigureAwait(false);
        return AnalyzeNode(analysis);
    }

    private static async Task<object?> QueryAsync(
        JsonElement args,
        ToolRunContext exec,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        ISyntaxTreeProvider? syntaxTreeProvider)
    {
        var (workspace, path) = Resolve(args, exec, workspaces, syntaxTreeProvider);
        var operation = LspJson.String(args, "operation") ?? "hover";
        var position = Position(args);
        switch (operation)
        {
            case "analyze":
                return AnalyzeNode(await workspace.AnalyzeAndIndexFileAsync(path, null, exec.Signal).ConfigureAwait(false));
            case "outline":
                var outline = await workspace.AnalyzeAndIndexFileAsync(path, null, exec.Signal).ConfigureAwait(false);
                return new JsonObject { ["roots"] = Nodes(outline.Syntax.Roots) };
            case "definition":
                return Locations(await workspace.GetDefinitionAsync(path, position, exec.Signal).ConfigureAwait(false));
            case "references":
                var includeDeclaration = !args.TryGetProperty("include_declaration", out var includeElement)
                    || includeElement.ValueKind != JsonValueKind.False;
                return Locations(await workspace.GetReferencesAsync(
                    path,
                    position,
                    includeDeclaration,
                    exec.Signal).ConfigureAwait(false));
            case "hover":
                return new JsonObject { ["text"] = await workspace.GetHoverAsync(path, position, exec.Signal).ConfigureAwait(false) };
            case "diagnostics":
                return Diagnostics(await workspace.GetDiagnosticsAsync(path, exec.Signal).ConfigureAwait(false));
            case "highlights":
                return Highlights(await workspace.GetHighlightsAsync(path, position, exec.Signal).ConfigureAwait(false));
            case "rename":
                var newName = LspJson.String(args, "new_name") ?? throw new ArgumentException("new_name is required for rename");
                return Edit(await workspace.RenameAsync(path, position, newName, exec.Signal).ConfigureAwait(false));
            case "code_actions":
                var actions = await workspace.GetCodeActionsAsync(path, Range(args, position), exec.Signal).ConfigureAwait(false);
                return Actions(actions);
            case "workspace_symbols":
                var query = LspJson.String(args, "query") ?? throw new ArgumentException("query is required for workspace_symbols");
                var profile = LanguageCatalog.FindByPath(path) ?? throw new NotSupportedException($"no language profile for '{path}'");
                return Symbols(await workspace.GetWorkspaceSymbolsAsync(profile.Id, query, exec.Signal).ConfigureAwait(false));
            default:
                throw new ArgumentException($"unknown operation '{operation}'");
        }
    }

    private static (LspWorkspace Workspace, string Path) Resolve(
        JsonElement args,
        ToolRunContext exec,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        ISyntaxTreeProvider? syntaxTreeProvider)
    {
        var path = LspJson.String(args, "path") ?? throw new ArgumentException("path is required");
        var root = LspJson.String(args, "root")
            ?? exec.Agent?.Session.WorkingDirectory
            ?? Environment.CurrentDirectory;
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var workspace = workspaces.GetOrAdd(
            Path.GetFullPath(root),
            location => new LspWorkspace(location, syntaxTreeProvider: syntaxTreeProvider));
        return (workspace, fullPath);
    }

    private static LspPosition Position(JsonElement args)
    {
        var line = LspJson.Int32(args, "line") ?? 1;
        var character = LspJson.Int32(args, "character") ?? 1;
        return new LspPosition(Math.Max(0, line - 1), Math.Max(0, character - 1));
    }

    private static LspRange Range(JsonElement args, LspPosition start)
        => new(
            start,
            new LspPosition(
                Math.Max(0, (LspJson.Int32(args, "end_line") ?? start.Line + 1) - 1),
                Math.Max(0, (LspJson.Int32(args, "end_character") ?? start.Character + 1) - 1)));

    private static JsonObject AnalyzeNode(DocumentAnalysis analysis)
        => new()
        {
            ["document"] = new JsonObject
            {
                ["uri"] = analysis.Document.Uri,
                ["version"] = analysis.Document.Version,
                ["languageId"] = analysis.Document.LanguageId,
            },
            ["roots"] = Nodes(analysis.Syntax.Roots),
            ["cst"] = analysis.Syntax.Cst is { } cst ? Cst(cst) : null,
            ["symbols"] = new JsonObject
            {
                ["nodes"] = new JsonArray(analysis.Symbols.Nodes.Select(SymbolNode).ToArray()),
                ["edges"] = new JsonArray(analysis.Symbols.Edges.Select(SymbolEdge).ToArray()),
            },
            ["scopes"] = new JsonArray(analysis.Scopes.Scopes.Select(ScopeNode).ToArray()),
            ["hierarchy"] = new JsonObject
            {
                ["nodes"] = new JsonArray(analysis.Hierarchy.Nodes.Select(HierarchyNode).ToArray()),
                ["edges"] = new JsonArray(analysis.Hierarchy.Edges.Select(HierarchyEdge).ToArray()),
            },
            ["types"] = new JsonObject
            {
                ["facts"] = new JsonArray(analysis.Types.Facts.Select(TypeFact).ToArray()),
                ["signatures"] = new JsonArray(analysis.Types.Signatures.Select(Signature).ToArray()),
                ["hints"] = new JsonArray(analysis.Types.Hints.Select(Hint).ToArray()),
            },
            ["diagnostics"] = Diagnostics(analysis.Diagnostics),
            ["validation"] = new JsonObject
            {
                ["issues"] = new JsonArray(analysis.Validation.Issues.Select(ValidationIssue).ToArray()),
            },
        };

    private static JsonArray Nodes(IReadOnlyList<SyntaxNode> nodes)
        => new(nodes.Select(Node).ToArray());

    private static JsonObject Node(SyntaxNode node)
        => new()
        {
            ["name"] = node.Name,
            ["kind"] = node.Kind,
            ["detail"] = node.Detail,
            ["range"] = RangeNode(node.Range),
            ["selectionRange"] = RangeNode(node.SelectionRange),
            ["children"] = Nodes(node.Children),
        };

    private static JsonObject SymbolNode(SymbolNode node)
        => new()
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["kind"] = node.Kind,
            ["detail"] = node.Detail,
            ["uri"] = node.Uri,
            ["range"] = RangeNode(node.Range),
        };

    private static JsonObject Cst(CstNode node)
        => new()
        {
            ["type"] = node.Type,
            ["field"] = node.FieldName,
            ["named"] = node.IsNamed,
            ["missing"] = node.IsMissing,
            ["extra"] = node.IsExtra,
            ["startByte"] = node.StartByte,
            ["endByte"] = node.EndByte,
            ["range"] = RangeNode(node.Range),
            ["children"] = new JsonArray(node.Children.Select(child => (JsonNode?)Cst(child)).ToArray()),
        };

    private static JsonObject SymbolEdge(SymbolEdge edge)
        => new()
        {
            ["from"] = edge.FromId,
            ["kind"] = edge.Kind.ToString(),
            ["uri"] = edge.Target.Uri,
            ["range"] = RangeNode(edge.Target.Range),
        };

    private static JsonObject ScopeNode(ScopeNode node)
        => new()
        {
            ["id"] = node.Id,
            ["kind"] = node.Kind,
            ["name"] = node.Name,
            ["parent"] = node.ParentId,
            ["declarations"] = new JsonArray(node.Declarations.Select(value => (JsonNode?)value).ToArray()),
        };

    private static JsonObject HierarchyNode(HierarchyNode node)
        => new()
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["kind"] = node.Kind,
            ["uri"] = node.Uri,
            ["range"] = RangeNode(node.Range),
        };

    private static JsonObject HierarchyEdge(HierarchyEdge edge)
        => new()
        {
            ["from"] = edge.FromId,
            ["kind"] = edge.Kind.ToString(),
            ["target"] = HierarchyNode(edge.Target),
        };

    private static JsonObject TypeFact(TypeFact fact)
        => new()
        {
            ["symbolId"] = fact.SymbolId,
            ["display"] = fact.Display,
            ["source"] = fact.SourceMethod,
            ["range"] = fact.Range is { } range ? RangeNode(range) : null,
        };

    private static JsonObject Signature(SignatureFact fact)
        => new()
        {
            ["symbolId"] = fact.SymbolId,
            ["label"] = fact.Label,
            ["documentation"] = fact.Documentation,
            ["activeParameter"] = fact.ActiveParameter,
        };

    private static JsonObject Hint(InlayHintFact hint)
        => new()
        {
            ["label"] = hint.Label,
            ["range"] = RangeNode(hint.Range),
            ["tooltip"] = hint.Tooltip,
        };

    private static JsonArray Diagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
        => new(diagnostics.Select(diagnostic => (JsonNode?)new JsonObject
        {
            ["range"] = RangeNode(diagnostic.Range),
            ["severity"] = (int)diagnostic.Severity,
            ["code"] = diagnostic.Code,
            ["source"] = diagnostic.Source,
            ["message"] = diagnostic.Message,
            ["codeDescription"] = diagnostic.CodeDescription,
            ["tags"] = diagnostic.Tags.Count == 0
                ? null
                : new JsonArray(diagnostic.Tags.Select(tag => (JsonNode?)tag).ToArray()),
            ["relatedInformation"] = diagnostic.RelatedInformation is { Count: > 0 } related
                ? new JsonArray(related.Select(item => (JsonNode?)new JsonObject
                {
                    ["message"] = item.Message,
                    ["uri"] = item.Location.Uri,
                    ["range"] = RangeNode(item.Location.Range),
                }).ToArray())
                : null,
        }).ToArray());

    private static JsonArray Locations(IReadOnlyList<LspLocation> locations)
        => new(locations.Select(location => (JsonNode?)new JsonObject
        {
            ["uri"] = location.Uri,
            ["range"] = RangeNode(location.Range),
        }).ToArray());

    private static JsonArray Highlights(IReadOnlyList<DocumentHighlightFact> highlights)
        => new(highlights.Select(highlight => (JsonNode?)new JsonObject
        {
            ["range"] = RangeNode(highlight.Range),
            ["kind"] = highlight.Kind,
        }).ToArray());

    private static JsonObject? Edit(WorkspaceEditFact? edit)
        => edit is null
            ? null
            : new JsonObject
            {
                ["documents"] = new JsonArray(edit.Documents.Select(document => (JsonNode?)new JsonObject
                {
                    ["uri"] = document.Uri,
                    ["edits"] = new JsonArray(document.Edits.Select(textEdit => (JsonNode?)new JsonObject
                    {
                        ["range"] = RangeNode(textEdit.Range),
                        ["newText"] = textEdit.NewText,
                    }).ToArray()),
                }).ToArray()),
            };

    private static JsonArray Actions(IReadOnlyList<CodeActionFact> actions)
        => new(actions.Select(action => (JsonNode?)new JsonObject
        {
            ["title"] = action.Title,
            ["kind"] = action.Kind,
            ["isPreferred"] = action.IsPreferred,
            ["disabled"] = action.IsDisabled,
            ["reason"] = action.DisabledReason,
            ["edit"] = Edit(action.Edit),
        }).ToArray());

    private static JsonArray Symbols(IReadOnlyList<WorkspaceSymbolFact> symbols)
        => new(symbols.Select(symbol => (JsonNode?)new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = symbol.Kind,
            ["container"] = symbol.ContainerName,
            ["uri"] = symbol.Uri,
            ["range"] = RangeNode(symbol.Range),
        }).ToArray());

    private static JsonObject ValidationIssue(ValidationIssue issue)
        => new()
        {
            ["code"] = issue.Code,
            ["message"] = issue.Message,
            ["range"] = issue.Range is { } range ? RangeNode(range) : null,
        };

    private static JsonObject RangeNode(LspRange range)
        => new()
        {
            ["start"] = PositionNode(range.Start),
            ["end"] = PositionNode(range.End),
        };

    private static JsonObject PositionNode(LspPosition position)
        => new()
        {
            ["line"] = position.Line,
            ["character"] = position.Character,
        };

    private static JsonObject Parameters()
        => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Source file path, relative to root when not absolute." },
                ["root"] = new JsonObject { ["type"] = "string", ["description"] = "Workspace root. Defaults to the session working directory." },
                ["operation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        "analyze",
                        "outline",
                        "definition",
                        "references",
                        "hover",
                        "diagnostics",
                        "highlights",
                        "rename",
                        "code_actions",
                        "workspace_symbols"),
                },
                ["line"] = new JsonObject { ["type"] = "number", ["description"] = "1-based line." },
                ["character"] = new JsonObject { ["type"] = "number", ["description"] = "1-based UTF-16 column." },
                ["end_line"] = new JsonObject { ["type"] = "number", ["description"] = "1-based end line." },
                ["end_character"] = new JsonObject { ["type"] = "number", ["description"] = "1-based end column." },
                ["query"] = new JsonObject { ["type"] = "string" },
                ["new_name"] = new JsonObject { ["type"] = "string" },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Unsaved file text for analyze." },
                ["include_declaration"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray("path"),
        };

    private static ToolOutputDefinition Output()
        => new(
            new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
            (_, value) => [new TextBlock(value.GetRawText())]);

    private sealed class Registration(
        IReadOnlyList<IDisposable> registrations,
        ConcurrentDictionary<string, LspWorkspace> workspaces) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
                registration.Dispose();
            foreach (var workspace in workspaces.Values)
                workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
