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
        var related = LspJson.Items(LspJson.Property(item, "relatedInformation"))
            .Select(relatedItem =>
            {
                var location = LspJson.Property(relatedItem, "location");
                var uri = LspJson.String(location, "uri");
                var range = LspJson.Range(LspJson.Property(location, "range"));
                return uri is null || range is null
                    ? null
                    : new LspDiagnosticRelatedInformation(
                        LspJson.String(relatedItem, "message") ?? string.Empty,
                        new LspLocation(uri, range.Value));
            })
            .Where(relatedItem => relatedItem is not null)
            .Select(relatedItem => relatedItem!)
            .ToArray();
        return new LspDiagnostic(
            LspJson.Range(LspJson.Property(item, "range")) ?? new LspRange(LspPosition.Zero, LspPosition.Zero),
            (LspDiagnosticSeverity)severity,
            codeText,
            LspJson.String(item, "source"),
            LspJson.String(item, "message") ?? string.Empty,
            tags,
            LspJson.Property(item, "data"),
            related,
            LspJson.String(LspJson.Property(item, "codeDescription"), "href"));
    }

    public static IReadOnlyList<LspDiagnostic> ParseMany(JsonElement? items)
        => LspJson.Items(items).Select(Parse).ToArray();
}
