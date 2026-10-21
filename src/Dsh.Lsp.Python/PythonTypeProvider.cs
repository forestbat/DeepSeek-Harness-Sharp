using System.Text.Json;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Python;

public sealed class PythonTypeProvider : IStructuredTypeProvider
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

def position(node):
    line = node.lineno - 1
    end_line = getattr(node, "end_lineno", node.lineno) - 1
    return line, character(node.lineno, node.col_offset), end_line, character(getattr(node, "end_lineno", node.lineno), getattr(node, "end_col_offset", node.col_offset))

def contains(node):
    if not hasattr(node, "lineno"):
        return False
    start_line, start_col, end_line, end_col = position(node)
    line = target["line"]
    col = target["character"]
    return (line, col) >= (start_line, start_col) and (line, col) <= (end_line, end_col)

def type_expr(node, inferred=False):
    if node is None:
        return None
    if isinstance(node, ast.Constant):
        value = node.value
        name = type(value).__name__
        return {"display": name, "kind": "Primitive", "name": name, "isInferred": inferred}
    if isinstance(node, ast.Name):
        name = node.id
        primitive = name in {"int", "float", "str", "bool", "bytes", "complex", "None"}
        return {"display": name, "kind": "Primitive" if primitive else "Named", "name": name, "isInferred": inferred}
    if isinstance(node, ast.Attribute):
        return {"display": ast.unparse(node), "kind": "Named", "name": node.attr, "isInferred": inferred}
    if isinstance(node, ast.Subscript):
        base = ast.unparse(node.value)
        args = node.slice.elts if isinstance(node.slice, ast.Tuple) else [node.slice]
        names = [ast.unparse(item) for item in args]
        if base in {"Optional", "typing.Optional"}:
            return {"display": ast.unparse(node), "kind": "Nullable", "name": "Optional", "isNullable": True, "isInferred": inferred}
        kind = "Generic"
        return {"display": ast.unparse(node), "kind": kind, "name": base, "arguments": [type_expr(item, inferred) for item in args if hasattr(item, "lineno")], "isInferred": inferred}
    if isinstance(node, (ast.Tuple, ast.List, ast.Set)):
        kind = "Tuple" if isinstance(node, ast.Tuple) else "Generic"
        return {"display": ast.unparse(node), "kind": kind, "name": type(node).__name__.lower(), "isInferred": inferred}
    text = ast.unparse(node)
    return {"display": text, "kind": "Named", "name": text, "isInferred": inferred}

tree = ast.parse(source)
candidates = [node for node in ast.walk(tree) if contains(node)]
if not candidates:
    print("null")
    sys.exit(0)
node = min(candidates, key=lambda item: (getattr(item, "end_lineno", 0) - item.lineno, getattr(item, "end_col_offset", 0) - item.col_offset))
name = node.attr if isinstance(node, ast.Attribute) else getattr(node, "id", None)
name = name or getattr(node, "arg", None)
if not name:
    print("null")
    sys.exit(0)
annotation = None
inferred = False
for item in ast.walk(tree):
    if isinstance(item, ast.AnnAssign):
        target_name = item.target.id if isinstance(item.target, ast.Name) else item.target.attr if isinstance(item.target, ast.Attribute) else None
        if target_name == name:
            annotation = item.annotation
            break
if annotation is None:
    for item in ast.walk(tree):
        if isinstance(item, ast.arg) and item.arg == name and item.annotation is not None:
            annotation = item.annotation
            break
if annotation is None:
    for item in ast.walk(tree):
        if isinstance(item, ast.Assign):
            target_names = [target.id for target in item.targets if isinstance(target, ast.Name)]
            if name in target_names:
                annotation = item.value
                inferred = True
                break
result = type_expr(annotation, inferred)
print(json.dumps(result))
""";

    public string Name => "python-ast-types";

    public async Task<TypeExpression?> ResolveAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("python", StringComparison.OrdinalIgnoreCase))
            return null;
        using var json = await PythonAstRuntime.RunAsync(
            Script,
            document,
            new { line = range.Start.Line, character = range.Start.Character },
            cancellationToken).ConfigureAwait(false);
        if (json is null || json.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var item = json.RootElement;
        return new TypeExpression(
            item.GetProperty("display").GetString() ?? string.Empty,
            Enum.TryParse<TypeExpressionKind>(item.GetProperty("kind").GetString(), out var kind)
                ? kind
                : TypeExpressionKind.Unknown,
            item.TryGetProperty("name", out var name) ? name.GetString() : null,
            IsNullable: item.TryGetProperty("isNullable", out var nullable) && nullable.GetBoolean(),
            IsDeclared: !(item.TryGetProperty("isInferred", out var inferred) && inferred.GetBoolean()),
            IsInferred: item.TryGetProperty("isInferred", out inferred) && inferred.GetBoolean());
    }
}
