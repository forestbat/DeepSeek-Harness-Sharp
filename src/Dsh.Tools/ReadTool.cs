using System.Text;
using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record ReadToolCaps
{
    public int Limit { get; init; } = 2000;
    public int MaxLineLength { get; init; } = 2000;
    public int MaxBytes { get; init; } = 50 * 1024;
}

public sealed record ReadFileLine(int Number, string Text);

public sealed record ReadResultValue(string Path, int Offset, IReadOnlyList<ReadFileLine> Lines, int TotalLines);

public static class ReadTool
{
    public const string ToolName = "read";

    private const string SectionText = "Use the read tool — not shell commands like cat — to inspect text files. Results include line numbers. Use offset and limit to continue reading large files.";

    public static IDisposable Register(Context ctx, ReadToolCaps? caps = null)
    {
        var resolved = caps ?? new ReadToolCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:read", PromptOrders.ToolRead, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Read a UTF-8 text file and return line-numbered content.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["file_path"] = ToolSchemas.StringParam("Path to read, resolved by the filesystem backend."),
                    ["offset"] = ToolSchemas.NumberParam("1-based first line to return. Defaults to 1."),
                    ["limit"] = ToolSchemas.NumberParam($"Maximum number of lines to return. Defaults to {resolved.Limit}."),
                },
                "file_path"),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path", "offset", "lines", "totalLines"],
                      "properties": {
                        "path": { "type": "string" },
                        "offset": { "type": "integer" },
                        "lines": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["number", "text"],
                            "properties": {
                              "number": { "type": "integer" },
                              "text": { "type": "string" }
                            }
                          }
                        },
                        "totalLines": { "type": "integer" }
                      }
                    }
                    """),
                (args, value) => Render(args, value, resolved)),
            Execute = (args, exec) => Execute(args, exec, resolved),
            IsConcurrencySafe = _ => true,
        });
        return new CompositeDisposable(section, registration);
    }

    private static (string FilePath, int Offset, int Limit) ParseArgs(JsonElement args, int maxLimit)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        if (filePath.Trim().Length == 0)
            throw new ArgumentException("file_path must be a non-empty string");
        var offset = ParsePositiveInteger(args, "offset") ?? 1;
        var limit = ParsePositiveInteger(args, "limit") ?? maxLimit;
        if (limit > maxLimit)
            throw new ArgumentException($"limit must be less than or equal to {maxLimit}");
        return (filePath, offset, limit);
    }

    private static int? ParsePositiveInteger(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        var value = element.GetDouble();
        if (!double.IsFinite(value) || value % 1 != 0 || value < 1)
            throw new ArgumentException($"{name} must be a positive integer");
        return (int)value;
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec, ReadToolCaps caps)
    {
        var (filePath, offset, limit) = ParseArgs(args, caps.Limit);
        var target = WorkspacePath.Resolve(exec, filePath);
        exec.Signal.ThrowIfCancellationRequested();
        if (!File.Exists(target))
            throw new HarnessException($"file not found: \"{target}\"", "FS_NOT_FOUND");
        var content = File.ReadAllText(target, new UTF8Encoding(false, false));
        var window = BuildWindow(content, offset, limit, caps, target);
        return Task.FromResult<object?>(new ReadResultValue(target, offset, window.Lines, window.TotalLines));
    }

    private static IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value, ReadToolCaps caps)
    {
        var result = value.Deserialize<ReadResultValue>(DshJson.Options)
            ?? throw new JsonException("read result value is malformed");
        var input = ParseArgs(args, caps.Limit);
        var endLine = result.Lines.Count > 0 ? result.Lines[^1].Number : Math.Max(0, result.Offset - 1);
        var truncatedByBytes = result.Lines.Count < input.Limit && endLine < result.TotalLines;
        return [new TextBlock(FormatReadOutput(result.Path, result, truncatedByBytes))];
    }

    internal static string FormatReadOutput(string displayPath, ReadResultValue outcome, bool truncatedByBytes)
    {
        var endLine = outcome.Lines.Count > 0 ? outcome.Lines[^1].Number : Math.Max(0, outcome.Offset - 1);
        string footer;
        if (truncatedByBytes)
            footer = $"(Output capped. Showing lines {outcome.Offset}-{endLine}. Use offset={endLine + 1} to continue.)";
        else if (endLine < outcome.TotalLines)
            footer = $"(Showing lines {outcome.Offset}-{endLine} of {outcome.TotalLines}. Use offset={endLine + 1} to continue.)";
        else
            footer = $"(End of file - total {outcome.TotalLines} lines)";
        var body = outcome.Lines.Count > 0
            ? $"{string.Join('\n', outcome.Lines.Select(line => $"{line.Number}: {line.Text}"))}\n\n{footer}"
            : footer;
        return $"<path>{displayPath}</path>\n<type>file</type>\n<content>\n{body}\n</content>";
    }

    private static (IReadOnlyList<ReadFileLine> Lines, int TotalLines) BuildWindow(string content, int offset, int limit, ReadToolCaps caps, string displayPath)
    {
        var rawLines = content.Split('\n');
        var lineCount = content.Length > 0 && rawLines[^1] == "" ? rawLines.Length - 1 : rawLines.Length;
        var lines = new List<ReadFileLine>();
        var outputBytes = 0;
        var truncatedByBytes = false;
        var totalLines = 0;
        for (var index = 0; index < lineCount; index++)
        {
            var raw = rawLines[index];
            if (raw.EndsWith('\r')) raw = raw[..^1];
            totalLines += 1;
            if (truncatedByBytes || totalLines < offset || lines.Count >= limit)
                continue;
            var text = raw.Length > caps.MaxLineLength
                ? $"{raw[..caps.MaxLineLength]}... (line truncated to {caps.MaxLineLength} chars)"
                : raw;
            var bytes = Encoding.UTF8.GetByteCount(text) + (lines.Count > 0 ? 1 : 0);
            if (outputBytes + bytes > caps.MaxBytes)
            {
                truncatedByBytes = true;
                continue;
            }
            outputBytes += bytes;
            lines.Add(new ReadFileLine(totalLines, text));
        }
        if (!truncatedByBytes && offset > totalLines && !(totalLines == 0 && offset == 1))
            throw new HarnessException($"offset {offset} is out of range for \"{displayPath}\" ({totalLines} lines)", "FS_NOT_FOUND");
        return (lines, totalLines);
    }
}
