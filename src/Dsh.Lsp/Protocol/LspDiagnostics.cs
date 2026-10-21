using System.Text.Json;

namespace Dsh.Lsp.Protocol;

public static class LspDiagnostics
{
    public static LspDiagnostic Parse(JsonElement item)
    {
        var severity = LspJson.Int32(item, "severity") ?? (int)LspDiagnosticSeverity.Error;
        var code = LspJson.Property(item, "code");
        var codeText = code is { ValueKind: JsonValueKind.String } codeValue
            ? codeValue.GetString()
            : code is { ValueKind: JsonValueKind.Number } number ? number.GetRawText() : null;
        var tags = LspJson.Items(LspJson.Property(item, "tags"))
            .Where(tag => tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out _))
            .Select(tag => tag.GetInt32().ToString())
            .ToArray();
        return new LspDiagnostic(
            LspJson.Range(LspJson.Property(item, "range")) ?? new LspRange(LspPosition.Zero, LspPosition.Zero),
            (LspDiagnosticSeverity)severity,
            codeText,
            LspJson.String(item, "source"),
            LspJson.String(item, "message") ?? string.Empty,
            tags,
            LspJson.Property(item, "data"));
    }

    public static IReadOnlyList<LspDiagnostic> ParseMany(JsonElement? items)
        => LspJson.Items(items).Select(Parse).ToArray();
}
