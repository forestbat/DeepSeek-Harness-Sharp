using System.Text;
using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record EditResultValue(string Path, string Before, string After);

public static class EditTool
{
    public const string ToolName = "edit";

    private const string SectionText = "Use the edit tool for targeted changes to existing UTF-8 text files. It replaces literal old_string with new_string; by default old_string must appear exactly once. If old_string appears multiple times, provide a more specific old_string or set replace_all to true. Read the file first (the default fs-observation-policy requires it), unless you just created or edited it in this session.";

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:edit", PromptOrders.ToolEdit, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Edit an existing UTF-8 text file by replacing literal text.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["file_path"] = ToolSchemas.StringParam("Path to edit, resolved by the filesystem backend."),
                    ["old_string"] = ToolSchemas.StringParam("Literal text to replace. Must match exactly."),
                    ["new_string"] = ToolSchemas.StringParam("Literal replacement text. Use an empty string to delete the match."),
                    ["replace_all"] = ToolSchemas.BooleanParam("Replace all matches. Defaults to false; when false, old_string must appear exactly once."),
                },
                "file_path", "old_string", "new_string"),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path", "before", "after"],
                      "properties": {
                        "path": { "type": "string" },
                        "before": { "type": "string" },
                        "after": { "type": "string" }
                      }
                    }
                    """),
                (args, value) =>
                {
                    var result = value.Deserialize<EditResultValue>(DshJson.Options)
                        ?? throw new JsonException("edit result value is malformed");
                    var replaceAll = args.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
                    return [new TextBlock(FormatEditOutput(result.Path, replaceAll))];
                }),
            Execute = Execute,
        });
        return new CompositeDisposable(section, registration);
    }

    internal static string FormatEditOutput(string displayPath, bool replaceAll)
        => replaceAll
            ? $"The file {displayPath} has been updated. All occurrences were successfully replaced."
            : $"The file {displayPath} has been updated successfully.";

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        var oldString = args.GetProperty("old_string").GetString() ?? "";
        var newString = args.GetProperty("new_string").GetString() ?? "";
        var replaceAll = args.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (filePath.Trim().Length == 0)
            throw new ArgumentException("file_path must be a non-empty string");
        if (oldString.Length == 0)
            throw new ArgumentException("old_string must be a non-empty string");
        if (oldString == newString)
            throw new ArgumentException("old_string and new_string must differ");
        var target = WorkspacePath.Resolve(exec, filePath);
        exec.Signal.ThrowIfCancellationRequested();
        if (!File.Exists(target))
            throw new HarnessException($"file not found: \"{target}\"", "FS_NOT_FOUND");
        var raw = await File.ReadAllTextAsync(target, new UTF8Encoding(false, false), exec.Signal);
        var lineEndings = DetectLineEndings(raw);
        var content = NormalizeLineEndings(raw);
        var (edited, _) = ApplyLiteralEdit(content, oldString, newString, replaceAll, target);
        var written = RestoreLineEndings(edited, lineEndings);
        var tempPath = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, written, new UTF8Encoding(false, false), exec.Signal);
            File.Move(tempPath, target, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        return new EditResultValue(target, content, edited);
    }

    internal static (string Content, int Replacements) ApplyLiteralEdit(string content, string oldString, string newString, bool replaceAll, string displayPath)
    {
        var oldNorm = NormalizeLineEndings(oldString);
        if (oldNorm.Length == 0)
            throw new HarnessException("old_string must be a non-empty string", "FS_EDIT_NOT_FOUND");
        var newNorm = NormalizeLineEndings(newString);
        var replacements = CountOccurrences(content, oldNorm);
        if (replacements == 0)
            throw new HarnessException($"old_string was not found in \"{displayPath}\"", "FS_EDIT_NOT_FOUND");
        if (!replaceAll && replacements > 1)
            throw new HarnessException(
                $"old_string matched {replacements} times in \"{displayPath}\"; provide a more specific old_string or set replace_all to true",
                "FS_AMBIGUOUS_EDIT");
        return (content.Replace(oldNorm, newNorm, StringComparison.Ordinal), replacements);
    }

    internal static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n");

    private static bool DetectLineEndings(string raw)
    {
        var sample = raw[..Math.Min(4096, raw.Length)];
        var crlfCount = CountOccurrences(sample, "\r\n");
        var lfCount = CountOccurrences(sample, "\n") - crlfCount;
        return crlfCount > lfCount;
    }

    private static string RestoreLineEndings(string content, bool crlf)
        => !crlf ? content : NormalizeLineEndings(content).Replace("\n", "\r\n");

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            var found = content.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0) return count;
            count += 1;
            index = found + needle.Length;
        }
    }
}
