using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Lsp.Analysis;
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

    public static IDisposable Register(
        Context context,
        LspProviderRegistry? providers = null)
    {
        var tools = context.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return new NoopDisposable();

        var workspaces = new ConcurrentDictionary<string, LspWorkspace>(StringComparer.OrdinalIgnoreCase);
        void ResetWorkspaces()
        {
            foreach (var workspace in workspaces.Values)
                workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            workspaces.Clear();
        }

        if (providers is not null)
            providers.Changed += ResetWorkspaces;
        var registrations = new List<IDisposable>
        {
            tools.Register(new ToolDefinition
            {
                Name = AnalyzeTool,
                Description = "Return a shallow outline and diagnostics by default; request symbols, scopes, types, hierarchy, semantic_tokens, cst, or all through layers when needed.",
                Parameters = Parameters(),
                Output = Output(),
                Execute = (args, exec) => AnalyzeAsync(args, exec, workspaces, providers),
                IsConcurrencySafe = _ => true,
            }),
            tools.Register(new ToolDefinition
            {
                Name = QueryTool,
                Description = "Query a language server for definition, references, hover, diagnostics, highlights, rename edits, code actions, outline, or workspace symbols.",
                Parameters = Parameters(),
                Output = Output(),
                Execute = (args, exec) => QueryAsync(args, exec, workspaces, providers),
                IsConcurrencySafe = _ => true,
            }),
        };
        return new Registration(
            registrations,
            workspaces,
            () =>
            {
                if (providers is not null)
                    providers.Changed -= ResetWorkspaces;
            });
    }

    private static async Task<object?> AnalyzeAsync(
        JsonElement args,
        ToolRunContext exec,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        LspProviderRegistry? providers)
    {
        var (workspace, path) = Resolve(args, exec, workspaces, providers);
        var text = LspJson.String(args, "text");
        var layerSet = Layers(args);
        var snapshot = await workspace.GetSnapshotAsync(path, text, exec.Signal).ConfigureAwait(false);
        if (layerSet.Contains("selection"))
        {
            var selected = await workspace.GetSelectionSnapshotAsync(
                path,
                [Position(args)],
                text,
                exec.Signal).ConfigureAwait(false);
            snapshot = snapshot with { Syntax = selected };
        }
        var symbols = layerSet.Contains("symbols")
            ? await workspace.GetSymbolsAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var scopes = layerSet.Contains("scopes")
            ? await workspace.GetScopesAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var hierarchy = layerSet.Contains("hierarchy")
            ? await workspace.GetHierarchyAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var types = layerSet.Contains("types")
            ? await workspace.GetTypesAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var tokens = layerSet.Contains("semantic_tokens")
            ? await workspace.GetSemanticTokensAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var cst = layerSet.Contains("cst")
            ? await workspace.GetCstAsync(path, text, exec.Signal).ConfigureAwait(false)
            : null;
        var zones = layerSet.Contains("zones")
            ? await workspace.GetZoneViewsAsync(
                path,
                text,
                includeSymbols: layerSet.Contains("symbols"),
                includeTypes: layerSet.Contains("types"),
                cancellationToken: exec.Signal).ConfigureAwait(false)
            : null;
        var flow = layerSet.Contains("flow")
            ? await workspace.GetFlowAsync(path, Range(args, Position(args)), text, exec.Signal).ConfigureAwait(false)
            : null;
        return AnalyzeNode(
            snapshot,
            symbols,
            scopes,
            hierarchy,
            types,
            tokens,
            cst,
            zones,
            flow,
            layerSet.Contains("validation") ? workspace.Index.Validate() : null);
    }

    private static async Task<object?> QueryAsync(
        JsonElement args,
        ToolRunContext exec,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        LspProviderRegistry? providers)
    {
        var (workspace, path) = Resolve(args, exec, workspaces, providers);
        var operation = LspJson.String(args, "operation") ?? "hover";
        var position = Position(args);
        switch (operation)
        {
            case "analyze":
                return await AnalyzeAsync(args, exec, workspaces, providers).ConfigureAwait(false);
            case "outline":
                var outline = await workspace.GetSnapshotAsync(path, null, exec.Signal).ConfigureAwait(false);
                return new JsonObject { ["roots"] = Nodes(outline.Syntax.Roots) };
            case "selection":
                return new JsonObject
                {
                    ["chains"] = new JsonArray(
                        (await workspace.GetSelectionChainsAsync(path, [position], exec.Signal).ConfigureAwait(false))
                        .Select(chain => (JsonNode?)new JsonObject
                        {
                            ["position"] = PositionNode(chain.Position),
                            ["ranges"] = new JsonArray(chain.Ranges.Select(range => (JsonNode?)RangeNode(range)).ToArray()),
                        }).ToArray()),
                };
            case "stubs":
                var stubQuery = LspJson.String(args, "query") ?? string.Empty;
                return new JsonObject
                {
                    ["stubs"] = new JsonArray(
                        workspace.FindStubs(stubQuery)
                            .Select(stub => (JsonNode?)new JsonObject
                            {
                                ["id"] = stub.Id,
                                ["name"] = stub.Name,
                                ["kind"] = stub.Kind,
                                ["uri"] = stub.Uri,
                                ["range"] = RangeNode(stub.Range),
                                ["selectionRange"] = RangeNode(stub.SelectionRange),
                                ["version"] = stub.DocumentVersion,
                                ["source"] = stub.Source,
                                ["visibility"] = stub.Visibility,
                                ["partial"] = stub.IsPartial,
                                ["genericParameters"] = new JsonArray(stub.GenericParameters?.Select(parameter => (JsonNode?)parameter).ToArray() ?? []),
                                ["generated"] = stub.IsGenerated,
                            }).ToArray()),
                };
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
            case "symbol_references":
                return new JsonObject
                {
                    ["edges"] = new JsonArray(
                        (await workspace.ResolveSymbolAsync(path, position, null, exec.Signal).ConfigureAwait(false))
                        .Select(edge => (JsonNode?)SymbolEdge(edge)).ToArray()),
                };
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
        LspProviderRegistry? providers)
    {
        var path = LspJson.String(args, "path") ?? throw new ArgumentException("path is required");
        var root = LspJson.String(args, "root")
            ?? exec.Agent?.Session.WorkingDirectory
            ?? Environment.CurrentDirectory;
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var workspace = workspaces.GetOrAdd(
            $"{Path.GetFullPath(root)}|{providers?.Version ?? 0}",
            location =>
            {
                var snapshot = providers?.Snapshot();
                return new LspWorkspace(
                    location,
                    syntaxTreeProvider: snapshot?.SyntaxTree,
                    structuredTypeProvider: snapshot?.StructuredType,
                    flowProvider: snapshot?.Flow,
                    declarationProvider: snapshot?.Declarations,
                    referenceProvider: snapshot?.References,
                    zoneProvider: snapshot?.Zone);
            });
        return (workspace, fullPath);
    }

    private static LspPosition Position(JsonElement args)
    {
        var line = LspJson.Int32(args, "line") ?? 1;
        var character = LspJson.Int32(args, "character") ?? 1;
        return new LspPosition(Math.Max(0, line - 1), Math.Max(0, character - 1));
    }

    private static HashSet<string> Layers(JsonElement args)
    {
        var layers = new HashSet<string>(StringComparer.Ordinal)
        {
            "outline",
            "diagnostics",
            "validation",
        };
        if (!args.TryGetProperty("layers", out var value) || value.ValueKind != JsonValueKind.Array)
            return layers;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                layers.Add(item.GetString() ?? string.Empty);
        }

        if (!layers.Contains("all"))
            return layers;
        layers.UnionWith(["symbols", "scopes", "hierarchy", "types", "semantic_tokens", "selection", "cst", "zones", "flow"]);
        return layers;
    }

    private static LspRange Range(JsonElement args, LspPosition start)
        => new(
            start,
            new LspPosition(
                Math.Max(0, (LspJson.Int32(args, "end_line") ?? start.Line + 1) - 1),
                Math.Max(0, (LspJson.Int32(args, "end_character") ?? start.Character + 1) - 1)));

    private static JsonObject AnalyzeNode(
        DocumentSnapshot snapshot,
        SymbolGraph? symbols,
        ScopeGraph? scopes,
        HierarchyGraph? hierarchy,
        TypeFactsSnapshot? types,
        IReadOnlyList<SemanticToken>? tokens,
        SyntaxSnapshot? cstSnapshot,
        IReadOnlyList<ZoneView>? zones,
        FlowGraph? flow,
        IReadOnlyList<ValidationIssue>? indexIssues)
        => new()
        {
            ["document"] = new JsonObject
            {
                ["uri"] = snapshot.Document.Uri,
                ["version"] = snapshot.Document.Version,
                ["languageId"] = snapshot.Document.LanguageId,
            },
            ["sourceKind"] = cstSnapshot?.SourceKind ?? snapshot.Syntax.SourceKind,
            ["partial"] = cstSnapshot?.IsPartial ?? snapshot.Syntax.IsPartial,
            ["roots"] = Nodes(cstSnapshot?.ParserRoots ?? snapshot.Syntax.ParserRoots ?? snapshot.Syntax.Roots),
            ["errors"] = Nodes(snapshot.Syntax.ErrorNodes),
            ["trivia"] = cstSnapshot is { TriviaNodes.Count: > 0 }
                ? new JsonArray(cstSnapshot.TriviaNodes.Select(node => (JsonNode?)new JsonObject
                {
                    ["type"] = node.Type,
                    ["range"] = RangeNode(node.Range),
                }).ToArray())
                : null,
            ["cstErrors"] = cstSnapshot is { CstErrorNodes.Count: > 0 }
                ? new JsonArray(cstSnapshot.CstErrorNodes.Select(node => (JsonNode?)new JsonObject
                {
                    ["type"] = node.Type,
                    ["range"] = RangeNode(node.Range),
                    ["missing"] = node.IsMissing,
                }).ToArray())
                : null,
            ["identities"] = new JsonArray(snapshot.Syntax.IdentityMatches.Select(match => (JsonNode?)new JsonObject
            {
                ["id"] = match.Id,
                ["lspRange"] = RangeNode(match.LspNode.Range),
                ["parserMatched"] = match.ParserNode is not null,
            }).ToArray()),
            ["selectionChains"] = snapshot.Syntax.SelectionChains is { Count: > 0 } chains
                ? new JsonArray(chains.Select(chain => (JsonNode?)new JsonObject
                {
                    ["position"] = PositionNode(chain.Position),
                    ["ranges"] = new JsonArray(chain.Ranges.Select(range => (JsonNode?)RangeNode(range)).ToArray()),
                }).ToArray())
                : null,
            ["cst"] = cstSnapshot?.Cst is { } cst ? Cst(cst) : null,
            ["symbols"] = symbols is null
                ? null
                : new JsonObject
                {
                    ["nodes"] = new JsonArray(symbols.Nodes.Select(SymbolNode).ToArray()),
                    ["edges"] = new JsonArray(symbols.Edges.Select(SymbolEdge).ToArray()),
                },
            ["scopes"] = scopes is null
                ? null
                : new JsonArray(scopes.Scopes.Select(ScopeNode).ToArray()),
            ["hierarchy"] = hierarchy is null
                ? null
                : new JsonObject
                {
                    ["nodes"] = new JsonArray(hierarchy.Nodes.Select(HierarchyNode).ToArray()),
                    ["edges"] = new JsonArray(hierarchy.Edges.Select(HierarchyEdge).ToArray()),
                },
            ["types"] = types is null
                ? null
                : new JsonObject
                {
                    ["facts"] = new JsonArray(types.Facts.Select(TypeFact).ToArray()),
                    ["signatures"] = new JsonArray(types.Signatures.Select(Signature).ToArray()),
                    ["hints"] = new JsonArray(types.Hints.Select(Hint).ToArray()),
                },
            ["semanticTokens"] = tokens is null
                ? null
                : new JsonArray(tokens.Select(token => (JsonNode?)new JsonObject
                {
                    ["range"] = RangeNode(token.Range),
                    ["type"] = token.Type,
                    ["modifiers"] = new JsonArray(token.Modifiers.Select(modifier => (JsonNode?)modifier).ToArray()),
                }).ToArray()),
            ["diagnostics"] = Diagnostics(snapshot.Diagnostics),
            ["zones"] = zones is { Count: > 0 }
                ? new JsonArray(zones.Select(zone => (JsonNode?)new JsonObject
                {
                    ["id"] = zone.Zone.Id,
                    ["languageId"] = zone.Zone.LanguageId,
                    ["range"] = zone.Zone.ParentRange is { } range ? RangeNode(range) : null,
                    ["symbols"] = zone.Symbols?.Nodes.Count ?? 0,
                    ["diagnostics"] = zone.Snapshot.Diagnostics.Count,
                    ["mappedSymbols"] = zone.MappedSymbols.Count,
                    ["mappedDiagnostics"] = zone.MappedDiagnostics.Count,
                    ["types"] = zone.Types?.Facts.Count ?? 0,
                }).ToArray())
                : null,
            ["validation"] = new JsonObject
            {
                ["issues"] = new JsonArray(
                    snapshot.Validation.Issues
                        .Concat(indexIssues ?? [])
                        .Select(ValidationIssue).ToArray()),
            },
            ["flow"] = flow is null
                ? null
                : new JsonObject
                {
                    ["entry"] = flow.Entry,
                    ["exit"] = flow.Exit,
                    ["nodes"] = new JsonArray(flow.Nodes.Select(node => (JsonNode?)new JsonObject
                    {
                        ["id"] = node.Id,
                        ["kind"] = node.Kind,
                        ["operations"] = node.Operations is { Count: > 0 }
                            ? new JsonArray(node.Operations.Select(operation => (JsonNode?)new JsonObject
                            {
                                ["kind"] = operation.Kind,
                                ["range"] = RangeNode(operation.Range),
                                ["implicit"] = operation.IsImplicit,
                            }).ToArray())
                            : null,
                    }).ToArray()),
                    ["edges"] = new JsonArray(flow.Edges.Select(edge => (JsonNode?)new JsonObject
                    {
                        ["from"] = edge.From,
                        ["to"] = edge.To,
                        ["kind"] = edge.Kind,
                    }).ToArray()),
                    ["variables"] = flow.Variables is { Count: > 0 }
                        ? new JsonArray(flow.Variables.Select(variable => (JsonNode?)new JsonObject
                        {
                            ["name"] = variable.Name,
                            ["definitions"] = new JsonArray(variable.Definitions.Select(range => (JsonNode?)RangeNode(range)).ToArray()),
                            ["uses"] = new JsonArray(variable.Uses.Select(range => (JsonNode?)RangeNode(range)).ToArray()),
                        }).ToArray())
                        : null,
                    ["exceptionEdges"] = flow.ExceptionEdges is { Count: > 0 } exceptions
                        ? new JsonArray(exceptions.Select(edge => (JsonNode?)new JsonObject
                        {
                            ["from"] = edge.From,
                            ["to"] = edge.To,
                            ["kind"] = edge.Kind,
                        }).ToArray())
                        : null,
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
            ["resolved"] = edge.IsResolved,
            ["unresolvedReason"] = edge.UnresolvedReason,
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
            ["expression"] = fact.Expression is { } expression ? TypeExpressionNode(expression) : null,
        };

    private static JsonObject Signature(SignatureFact fact)
        => new()
        {
            ["symbolId"] = fact.SymbolId,
            ["label"] = fact.Label,
            ["documentation"] = fact.Documentation,
            ["activeParameter"] = fact.ActiveParameter,
            ["parameters"] = fact.Parameters is { Count: > 0 } parameters
                ? new JsonArray(parameters.Select(parameter => (JsonNode?)new JsonObject
                {
                    ["label"] = parameter.Label,
                    ["documentation"] = parameter.Documentation,
                    ["type"] = parameter.Type is { } type ? TypeExpressionNode(type) : null,
                }).ToArray())
                : null,
            ["returnType"] = fact.ReturnType is { } returnType ? TypeExpressionNode(returnType) : null,
        };

    private static JsonObject TypeExpressionNode(TypeExpression expression)
        => new()
        {
            ["display"] = expression.Display,
            ["kind"] = expression.Kind.ToString(),
            ["name"] = expression.Name,
            ["nullable"] = expression.IsNullable,
            ["element"] = expression.Element is { } element ? TypeExpressionNode(element) : null,
            ["arguments"] = expression.Arguments is { Count: > 0 } arguments
                ? new JsonArray(arguments.Select(argument => (JsonNode?)TypeExpressionNode(argument)).ToArray())
                : null,
            ["elements"] = expression.Elements is { Count: > 0 } elements
                ? new JsonArray(elements.Select(item => (JsonNode?)TypeExpressionNode(item)).ToArray())
                : null,
            ["parameters"] = expression.Parameters is { Count: > 0 } parameters
                ? new JsonArray(parameters.Select(parameter => (JsonNode?)TypeExpressionNode(parameter)).ToArray())
                : null,
            ["returnType"] = expression.ReturnType is { } returnType ? TypeExpressionNode(returnType) : null,
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
            ["command"] = action.Command is { } command
                ? new JsonObject
                {
                    ["title"] = command.Title,
                    ["command"] = command.Command,
                    ["arguments"] = command.Arguments is { } arguments
                        ? JsonNode.Parse(arguments.GetRawText())
                        : null,
                }
                : null,
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
                        "selection",
                        "stubs",
                        "definition",
                        "references",
                        "symbol_references",
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
                ["layers"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Optional lazy layers: symbols, scopes, hierarchy, types, semantic_tokens, selection, cst, zones, flow, or all.",
                },
            },
            ["required"] = new JsonArray("path"),
        };

    private static ToolOutputDefinition Output()
        => new(
            new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
            (_, value) => [new TextBlock(value.GetRawText())]);

    private sealed class Registration(
        IReadOnlyList<IDisposable> registrations,
        ConcurrentDictionary<string, LspWorkspace> workspaces,
        Action unsubscribe) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
                registration.Dispose();
            unsubscribe();
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
