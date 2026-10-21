using System.Text.Json;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Python;

public sealed class PythonReferenceProvider : IReferenceProvider
{
    private const string Script = """
import ast, json, sys

data = json.load(sys.stdin)
source = data["source"]
target = data["payload"]
lines = source.splitlines()

def character(line, byte_column):
    if line < 1 or line > len(lines):
        return byte_column
    raw = lines[line - 1].encode("utf-8")[:byte_column]
    return len(raw.decode("utf-8", errors="ignore"))

def span(node):
    if not hasattr(node, "lineno"):
        return {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}
    return {
        "start": {"line": node.lineno - 1, "character": character(node.lineno, node.col_offset)},
        "end": {"line": getattr(node, "end_lineno", node.lineno) - 1, "character": character(getattr(node, "end_lineno", node.lineno), getattr(node, "end_col_offset", node.col_offset))},
    }

def contains(node):
    if not hasattr(node, "lineno"):
        return False
    item = span(node)
    line = target["line"]
    col = target["character"]
    return (line, col) >= (item["start"]["line"], item["start"]["character"]) and (line, col) <= (item["end"]["line"], item["end"]["character"])

tree = ast.parse(source)
candidates = [node for node in ast.walk(tree) if isinstance(node, (ast.Name, ast.Attribute, ast.arg, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and contains(node)]
if not candidates:
    print("null")
    sys.exit(0)
selected = min(candidates, key=lambda node: (getattr(node, "end_lineno", 0) - node.lineno, getattr(node, "end_col_offset", 0) - node.col_offset))
name = selected.attr if isinstance(selected, ast.Attribute) else getattr(selected, "id", None) or getattr(selected, "arg", None) or getattr(selected, "name", None)
if not name:
    print("null")
    sys.exit(0)

items = []
for node in ast.walk(tree):
    if isinstance(node, ast.Name) and node.id == name:
        items.append({"range": span(node), "kind": "Definition" if isinstance(node.ctx, ast.Store) else "Reference"})
    elif isinstance(node, ast.Attribute) and node.attr == name:
        items.append({"range": span(node), "kind": "Reference"})
    elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and node.name == name:
        items.append({"range": span(node), "kind": "Definition"})
    elif isinstance(node, ast.arg) and node.arg == name:
        items.append({"range": span(node), "kind": "Definition"})
print(json.dumps({"name": name, "items": items}))
""";

    public string Name => "python-ast-references";

    public async Task<IReadOnlyList<SymbolEdge>> ResolveAsync(
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("python", StringComparison.OrdinalIgnoreCase))
            return [];
        using var json = await PythonAstRuntime.RunAsync(
            Script,
            document,
            new { line = range.Start.Line, character = range.Start.Character },
            cancellationToken).ConfigureAwait(false);
        if (json is null || json.RootElement.ValueKind is JsonValueKind.Null)
            return [];
        var sourceName = $"{document.Uri}|{range.Start.Line}:{range.Start.Character}|{json.RootElement.GetProperty("name").GetString()}";
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => new SymbolEdge(
                sourceName,
                Enum.TryParse<SymbolEdgeKind>(item.GetProperty("kind").GetString(), out var kind)
                    ? kind
                    : SymbolEdgeKind.Reference,
                new LspLocation(document.Uri, ReadRange(item.GetProperty("range"))),
                Name))
            .DistinctBy(edge => $"{edge.Kind}|{edge.Target.Range.Start.Line}:{edge.Target.Range.Start.Character}")
            .ToArray();
    }

    private static LspRange ReadRange(JsonElement item)
        => new(
            new LspPosition(
                item.GetProperty("start").GetProperty("line").GetInt32(),
                item.GetProperty("start").GetProperty("character").GetInt32()),
            new LspPosition(
                item.GetProperty("end").GetProperty("line").GetInt32(),
                item.GetProperty("end").GetProperty("character").GetInt32()));
}
