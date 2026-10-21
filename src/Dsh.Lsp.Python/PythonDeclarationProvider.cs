using System.Diagnostics;
using System.Text.Json;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Python;

public sealed class PythonDeclarationProvider : IDeclarationProvider
{
    private const string Script = """
import ast, json, sys
source = sys.stdin.read()
tree = ast.parse(source)
items = []
for node in ast.walk(tree):
    if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
        items.append({
            "name": node.name,
            "kind": type(node).__name__,
            "line": node.lineno - 1,
            "character": node.col_offset,
            "endLine": getattr(node, "end_lineno", node.lineno) - 1,
            "endCharacter": getattr(node, "end_col_offset", node.col_offset),
        })
    elif isinstance(node, ast.Assign):
        for target in node.targets:
            if isinstance(target, ast.Name):
                items.append({
                    "name": target.id,
                    "kind": "Assign",
                    "line": node.lineno - 1,
                    "character": node.col_offset,
                    "endLine": getattr(node, "end_lineno", node.lineno) - 1,
                    "endCharacter": getattr(node, "end_col_offset", node.col_offset),
                })
print(json.dumps(items))
""";

    public string Name => "python-ast-declarations";

    public async Task<IReadOnlyList<StubDeclaration>> ExtractAsync(
        LspDocument document,
        CstNode? cst,
        SymbolGraph symbols,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("python", StringComparison.OrdinalIgnoreCase))
            return [];
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Script);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return [];
            await process.StandardInput.WriteAsync(document.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            if (process.ExitCode != 0)
                return [];
            using var json = JsonDocument.Parse(output);
            return json.RootElement.EnumerateArray()
                .Select(item => new StubDeclaration(
                    $"{document.Uri}|{item.GetProperty("line").GetInt32()}:{item.GetProperty("character").GetInt32()}|{item.GetProperty("name").GetString()}",
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("kind").GetString() ?? "Python",
                    document.Uri,
                    new LspRange(
                        new LspPosition(item.GetProperty("line").GetInt32(), item.GetProperty("character").GetInt32()),
                        new LspPosition(item.GetProperty("endLine").GetInt32(), item.GetProperty("endCharacter").GetInt32())),
                    new LspRange(
                        new LspPosition(item.GetProperty("line").GetInt32(), item.GetProperty("character").GetInt32()),
                        new LspPosition(item.GetProperty("line").GetInt32(), item.GetProperty("character").GetInt32())),
                    document.Version,
                    "python-ast"))
                .ToArray();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}
