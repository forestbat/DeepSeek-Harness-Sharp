using System.Text.Json;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Python;

public sealed class PythonFlowProvider : IFlowProvider
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
    line = node.lineno - 1
    end_line = getattr(node, "end_lineno", node.lineno) - 1
    return {
        "start": {"line": line, "character": character(node.lineno, node.col_offset)},
        "end": {"line": end_line, "character": character(getattr(node, "end_lineno", node.lineno), getattr(node, "end_col_offset", node.col_offset))},
    }

def contains(node):
    if not hasattr(node, "lineno"):
        return False
    item = span(node)
    line = target["line"]
    col = target["character"]
    return (line, col) >= (item["start"]["line"], item["start"]["character"]) and (line, col) <= (item["end"]["line"], item["end"]["character"])

tree = ast.parse(source)
functions = [node for node in ast.walk(tree) if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and contains(node)]
function = min(functions, key=lambda node: getattr(node, "end_lineno", 0) - node.lineno) if functions else None
body = function.body if function else tree.body
scope = function or tree

nodes = []
edges = []
exception_edges = []
variables = []
counter = 0

def add(kind, node, operation=None):
    global counter
    value = counter
    counter += 1
    nodes.append({"id": value, "kind": kind, "operations": [{"kind": operation or kind, "range": span(node), "isImplicit": False}] if operation or node is not None else []})
    return value

def edge(source_id, target_id, kind="next"):
    if source_id is None or target_id is None:
        return
    item = {"from": source_id, "to": target_id, "kind": kind}
    if item not in edges:
        edges.append(item)

entry = add("entry", scope)
exit_id = add("exit", scope)

def build_block(statements, incoming):
    current = incoming
    for statement in statements:
        if isinstance(statement, ast.If):
            condition = add("condition", statement.test)
            edge(current, condition)
            then_end = build_block(statement.body, condition)
            else_end = build_block(statement.orelse, condition)
            merge = add("merge", statement)
            edge(then_end, merge)
            edge(else_end, merge)
            current = merge
        elif isinstance(statement, (ast.For, ast.AsyncFor, ast.While)):
            loop = add("loop", statement)
            edge(current, loop)
            body_end = build_block(statement.body, loop)
            edge(body_end, loop, "back")
            current = loop
        elif isinstance(statement, (ast.Try, ast.TryStar)):
            try_node = add("try", statement)
            edge(current, try_node)
            body_end = build_block(statement.body, try_node)
            handler_end = None
            for handler in statement.handlers:
                handler_node = add("catch", handler)
                exception_edges.append({"from": try_node, "to": handler_node, "kind": "exception"})
                handler_end = build_block(handler.body, handler_node)
            finally_end = build_block(statement.finalbody, body_end)
            merge = add("merge", statement)
            edge(body_end, merge)
            edge(handler_end, merge)
            edge(finally_end, merge)
            current = merge
        elif isinstance(statement, (ast.Return, ast.Raise)):
            item = add(type(statement).__name__.lower(), statement)
            edge(current, item)
            edge(item, exit_id, "return")
            current = None
        elif isinstance(statement, (ast.Break, ast.Continue)):
            item = add(type(statement).__name__.lower(), statement)
            edge(current, item)
            current = item
        else:
            item = add(type(statement).__name__, statement)
            edge(current, item)
            current = item
    return current

end = build_block(body, entry)
edge(end, exit_id)

for node in ast.walk(scope):
    if isinstance(node, ast.arg):
        variables.append({"name": node.arg, "definitions": [span(node)], "uses": []})
    elif isinstance(node, ast.Name):
        existing = next((item for item in variables if item["name"] == node.id), None)
        if existing is None:
            existing = {"name": node.id, "definitions": [], "uses": []}
            variables.append(existing)
        if isinstance(node.ctx, ast.Store):
            existing["definitions"].append(span(node))
        else:
            existing["uses"].append(span(node))

print(json.dumps({"entry": entry, "exit": exit_id, "nodes": nodes, "edges": edges, "exceptionEdges": exception_edges, "variables": variables}))
""";

    public string Name => "python-ast-flow";

    public async Task<FlowGraph?> BuildAsync(
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
        if (json is null)
            return null;
        var item = json.RootElement;
        var nodes = item.GetProperty("nodes").EnumerateArray()
            .Select(node => new FlowNode(
                node.GetProperty("id").GetInt32(),
                node.GetProperty("kind").GetString() ?? "unknown",
                node.GetProperty("operations").EnumerateArray()
                    .Select(operation => new FlowOperation(
                        operation.GetProperty("kind").GetString() ?? "unknown",
                        ReadRange(operation.GetProperty("range")),
                        operation.GetProperty("isImplicit").GetBoolean()))
                    .ToArray()))
            .ToArray();
        var edges = item.GetProperty("edges").EnumerateArray()
            .Select(edge => new FlowEdge(
                edge.GetProperty("from").GetInt32(),
                edge.GetProperty("to").GetInt32(),
                edge.GetProperty("kind").GetString() ?? "next"))
            .ToArray();
        var exceptionEdges = item.GetProperty("exceptionEdges").EnumerateArray()
            .Select(edge => new FlowEdge(
                edge.GetProperty("from").GetInt32(),
                edge.GetProperty("to").GetInt32(),
                edge.GetProperty("kind").GetString() ?? "exception"))
            .ToArray();
        var variables = item.GetProperty("variables").EnumerateArray()
            .Select(variable => new FlowVariable(
                variable.GetProperty("name").GetString() ?? string.Empty,
                variable.GetProperty("definitions").EnumerateArray().Select(ReadRange).ToArray(),
                variable.GetProperty("uses").EnumerateArray().Select(ReadRange).ToArray()))
            .ToArray();
        return new FlowGraph(
            item.GetProperty("entry").GetInt32(),
            item.GetProperty("exit").GetInt32(),
            nodes,
            edges,
            variables,
            exceptionEdges);
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
