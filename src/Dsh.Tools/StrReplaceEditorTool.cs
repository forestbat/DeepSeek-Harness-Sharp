using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record StrReplaceEditorConfig
{
    public int MaxOutputChars { get; init; } = 16_000;
    public string? Description { get; init; }
}

// 逐字移植 packages/fs/tool-str-replace-editor/src/index.ts。
// 与 TS 的差异:C# 侧没有 sandbox fs 服务,直接做本地文件 I/O;
// fs/write-intent、fs/edit-intent waterfall 与 fs/observed 事件没有本地消费者,不发。
public static class StrReplaceEditorTool
{
    public const string ToolName = "str_replace_editor";

    private const string TruncatedMessage = "<response clipped><NOTE>To save on context only part of this file has been shown to you. You should retry this tool after you have searched inside the file with `grep -n` in order to find the line numbers of what you are looking for.</NOTE>";

    private const string DefaultDescription = """
        Custom editing tool for viewing, creating and editing files
        * State is persistent across command calls and discussions with the user
        * If `path` is a file, `view` displays the result of applying `cat -n`. If `path` is a directory, `view` lists non-hidden files and directories up to 2 levels deep
        * The `create` command cannot be used if the specified `path` already exists as a file
        * If a `command` generates a long output, it will be truncated and marked with `<response clipped>`
        * A null placeholder for a parameter unused by the selected command is treated as omitted. Required parameters still need values; omit `str_replace.new_str` rather than setting it to null when deleting a match

        Notes for using the `str_replace` command:
        * The `old_str` parameter should match EXACTLY one or more consecutive lines from the original file. Be mindful of whitespaces!
        * If the `old_str` parameter is not unique in the file, the replacement will not be performed. Make sure to include enough context in `old_str` to make it unique
        * The `new_str` parameter should contain the edited lines that should replace the `old_str`
        """;

    public static IDisposable Register(Context ctx, StrReplaceEditorConfig? config = null)
    {
        var resolved = config ?? new StrReplaceEditorConfig();
        if (resolved.MaxOutputChars <= 0)
            throw new ArgumentException("tool-str-replace-editor: maxOutputChars must be a positive safe integer");
        var description = resolved.Description ?? DefaultDescription;
        if (description.Trim().Length == 0)
            throw new ArgumentException("tool-str-replace-editor: description must be non-empty");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = description,
            Parameters = ToolSchemas.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["command", "path"],
                  "properties": {
                    "command": {
                      "type": "string",
                      "enum": ["view", "create", "str_replace", "insert"],
                      "description": "The commands to run. Allowed options are: `view`, `create`, `str_replace`, `insert`."
                    },
                    "path": {
                      "type": "string",
                      "description": "Absolute path to file or directory, e.g. `/repo/file.py` or `/repo`."
                    },
                    "file_text": {
                      "oneOf": [{ "type": "string" }, { "type": "null" }],
                      "description": "Required string parameter of `create` command, with the content of the file to be created. A null placeholder is treated as omitted by commands that do not use this parameter."
                    },
                    "insert_line": {
                      "oneOf": [{ "type": "integer" }, { "type": "null" }],
                      "description": "Required integer parameter of `insert` command. The `new_str` will be inserted AFTER the line `insert_line` of `path`. A null placeholder is treated as omitted by commands that do not use this parameter."
                    },
                    "new_str": {
                      "oneOf": [{ "type": "string" }, { "type": "null" }],
                      "description": "Optional string parameter of `str_replace` command containing the new string (if omitted, no string will be added). Required string parameter of `insert` command containing the string to insert. A null placeholder is accepted only by commands that do not use this parameter."
                    },
                    "old_str": {
                      "oneOf": [{ "type": "string" }, { "type": "null" }],
                      "description": "Required string parameter of `str_replace` command containing the string in `path` to replace. A null placeholder is treated as omitted by commands that do not use this parameter."
                    },
                    "view_range": {
                      "oneOf": [{ "type": "array", "items": { "type": "integer" } }, { "type": "null" }],
                      "description": "Optional parameter of `view` command when `path` points to a file. If omitted or null, the full file is shown. If provided, the file will be shown in the indicated line number range, e.g. [11, 12] will show lines 11 and 12. Indexing at 1 to start. Setting `[start_line, -1]` shows all lines from `start_line` to the end of the file."
                    }
                  }
                }
                """),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""{ "type": "string" }"""),
                (_, value) => [new TextBlock(value.GetString() ?? "")],
                (args, _) => PresentCall(args)),
            Execute = (args, exec) => Execute(args, exec, resolved),
        });
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec, StrReplaceEditorConfig config)
    {
        var command = args.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : null;
        var path = args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString() ?? ""
            : "";
        object? result = command switch
        {
            "view" => ViewPath(path, args, config.MaxOutputChars, exec),
            "create" => CreateFile(path, OptionalString(args, "file_text"), exec),
            "str_replace" => ReplaceInFile(path, OptionalString(args, "old_str"), NewStrOf(args), exec),
            "insert" => InsertInFile(path, OptionalInt(args, "insert_line"), OptionalString(args, "new_str"), exec),
            // TS 由入参 schema 拦截未知 command;C# 侧运行时校验入参 schema 未接,这里等价拒绝。
            _ => throw new ArgumentException($"invalid command: expected one of `view`, `create`, `str_replace`, `insert`, got {(command is null ? "missing" : $"`{command}`")}"),
        };
        return Task.FromResult<object?>(result);
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        return element.GetString() ?? "";
    }

    // str_replace 的 new_str 区分「缺省」(视为 '')与「显式 null」(报错),见 TS replaceInFile。
    private static (bool IsNull, string? Value) NewStrOf(JsonElement args)
        => args.TryGetProperty("new_str", out var element) && element.ValueKind == JsonValueKind.Null
            ? (true, null)
            : (false, OptionalString(args, "new_str"));

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new ArgumentException($"invalid `{name}`: expected an integer");
        return value;
    }

    private static int[]? ParseViewRange(JsonElement args)
    {
        if (!args.TryGetProperty("view_range", out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Invalid `view_range`. It should be a list of two integers.");
        var values = element.EnumerateArray().ToList();
        if (values.Count != 2 || values.Any(item => item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out _)))
            throw new ArgumentException("Invalid `view_range`. It should be a list of two integers.");
        return [values[0].GetInt32(), values[1].GetInt32()];
    }

    private static string RequiredForCommand(string? value, string parameter, string command, bool allowEmpty = true)
    {
        if (value is null)
            throw new ArgumentException($"Parameter `{parameter}` is required for command: {command}");
        if (!allowEmpty && value.Length == 0)
            throw new ArgumentException($"Parameter `{parameter}` is empty for command: {command}");
        return value;
    }

    private static string ResolveTarget(string path)
    {
        if (path.Trim().Length == 0)
            throw new ArgumentException("path must be a non-empty string");
        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"The path {path} is not an absolute path, it should start with `/`. Maybe you meant /{path}?");
        return Path.GetFullPath(path);
    }

    private static bool StatExisting(string target, string command)
    {
        if (File.Exists(target)) return false;
        if (Directory.Exists(target))
        {
            if (command != "view")
                throw new HarnessException($"The path {target} is a directory and only the `view` command can be used on directories", "FS_NOT_REGULAR_FILE");
            return true;
        }
        throw new HarnessException($"The path {target} does not exist. Please provide a valid path.", "FS_NOT_FOUND");
    }

    private static string MaybeTruncate(string content, int maxOutputChars)
        => content.Length <= maxOutputChars ? content : string.Concat(content.AsSpan(0, maxOutputChars), TruncatedMessage);

    private static string ViewPath(string path, JsonElement args, int maxOutputChars, ToolRunContext exec)
    {
        var target = ResolveTarget(path);
        if (StatExisting(target, "view"))
        {
            if (args.TryGetProperty("view_range", out var rangeElement) && rangeElement.ValueKind != JsonValueKind.Null)
                throw new ArgumentException("The `view_range` parameter is not allowed when `path` points to a directory.");
            return ListDirectory(target, maxOutputChars);
        }
        var viewRange = ParseViewRange(args);
        exec.Signal.ThrowIfCancellationRequested();
        var content = File.ReadAllText(target, new UTF8Encoding(false, false));
        return FormatFileView(target, content, maxOutputChars, viewRange);
    }

    private static string FormatFileView(string path, string content, int maxOutputChars, int[]? viewRange)
    {
        var allLines = content.Split('\n');
        var lines = allLines;
        var initialLine = 1;
        var prompt = $"Here's the content of {path} with line numbers (which has a total of {allLines.Length} lines)";
        if (viewRange is not null)
        {
            var requestedInitialLine = viewRange[0];
            var requestedFinalLine = viewRange[1];
            initialLine = requestedInitialLine;
            if (initialLine < 1 || initialLine > allLines.Length)
                throw new ArgumentException($"Invalid `view_range`: [{viewRange[0]}, {viewRange[1]}]. Its first element `{initialLine}` should be within the range of lines of the file: [1, {allLines.Length}]");
            if (requestedFinalLine > allLines.Length)
                throw new ArgumentException($"Invalid `view_range`: [{viewRange[0]}, {viewRange[1]}]. Its second element `{requestedFinalLine}` should be smaller than the number of lines in the file: `{allLines.Length}`");
            if (requestedFinalLine != -1 && requestedFinalLine < initialLine)
                throw new ArgumentException($"Invalid `view_range`: [{viewRange[0]}, {viewRange[1]}]. Its second element `{requestedFinalLine}` should be larger or equal than its first `{initialLine}`");
            lines = requestedFinalLine == -1 ? allLines[(initialLine - 1)..] : allLines[(initialLine - 1)..requestedFinalLine];
            prompt += $" with view_range=[{initialLine}, {requestedFinalLine}]";
        }
        var numbered = string.Join('\n', lines.Select((line, index) => $"{(initialLine + index).ToString().PadLeft(6)}  {line}"));
        return MaybeTruncate($"{prompt}:\n{numbered}\n", maxOutputChars);
    }

    private static string ListDirectory(string target, int maxOutputChars)
    {
        var rows = new List<string> { $"d\t{target}" };
        Visit(target, 1, rows);
        rows.Sort((left, right) => string.CompareOrdinal(RowKey(left), RowKey(right)));
        var listing = MaybeTruncate(string.Join('\n', rows) + '\n', maxOutputChars);
        return $"Here're the files and directories up to 2 levels deep in {target}, excluding hidden items, node_modules, and Python cache directories:\n{listing}\n";

        static string RowKey(string row) => row[(row.IndexOf('\t') + 1)..];
    }

    private static void Visit(string directory, int depth, List<string> rows)
    {
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.') || name is "node_modules" or "__pycache__") continue;
            var isDirectory = Directory.Exists(entry);
            rows.Add($"{(isDirectory ? "d" : "f")}\t{entry}");
            if (isDirectory && depth < 2)
                Visit(entry, depth + 1, rows);
        }
    }

    private static string CreateFile(string path, string? fileText, ToolRunContext exec)
    {
        var content = RequiredForCommand(fileText, "file_text", "create");
        var target = ResolveTarget(path);
        exec.Signal.ThrowIfCancellationRequested();
        if (File.Exists(target) || Directory.Exists(target))
            throw new ArgumentException($"File already exists at: {target}. Cannot overwrite files using command `create`.");
        var directory = Path.GetDirectoryName(target);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        File.WriteAllText(target, content, new UTF8Encoding(false, false));
        return $"New file created successfully at: {target}";
    }

    private static string ReplaceInFile(string path, string? oldStr, (bool IsNull, string? Value) newStr, ToolRunContext exec)
    {
        if (newStr.IsNull)
            throw new ArgumentException("Parameter `new_str` must be omitted or contain a string for command: str_replace");
        var target = ResolveTarget(path);
        var oldValue = RequiredForCommand(oldStr, "old_str", "str_replace", false);
        var newValue = newStr.Value ?? "";
        StatExisting(target, "str_replace");
        exec.Signal.ThrowIfCancellationRequested();
        var before = File.ReadAllText(target, new UTF8Encoding(false, false));
        var offsets = MatchOffsets(before, oldValue);
        if (offsets.Count == 0)
            throw new HarnessException($"No replacement was performed, old_str `{oldValue}` did not appear verbatim in {target}.", "FS_EDIT_NOT_FOUND");
        if (offsets.Count > 1)
        {
            var lines = LineNumbersAt(before, offsets);
            throw new HarnessException($"No replacement was performed. Multiple occurrences of old_str `{oldValue}` in lines [{string.Join(", ", lines)}]. Please ensure it is unique", "FS_AMBIGUOUS_EDIT");
        }
        var offset = offsets[0];
        File.WriteAllText(target, string.Concat(before.AsSpan(0, offset), newValue, before.AsSpan(offset + oldValue.Length)), new UTF8Encoding(false, false));
        return $"The file {target} has been edited successfully.";
    }

    private static string InsertInFile(string path, int? insertLine, string? newStr, ToolRunContext exec)
    {
        if (insertLine is null)
            throw new ArgumentException("Parameter `insert_line` is required for command: insert");
        var value = RequiredForCommand(newStr, "new_str", "insert");
        var target = ResolveTarget(path);
        StatExisting(target, "insert");
        exec.Signal.ThrowIfCancellationRequested();
        var before = File.ReadAllText(target, new UTF8Encoding(false, false));
        var lines = before.Split('\n');
        if (insertLine.Value < 0 || insertLine.Value > lines.Length)
            throw new ArgumentException($"Invalid `insert_line` parameter: {insertLine.Value}. It should be within the range of lines of the file: [0, {lines.Length}]");
        var after = string.Join('\n', lines.Take(insertLine.Value).Concat(value.Split('\n')).Concat(lines.Skip(insertLine.Value)));
        File.WriteAllText(target, after, new UTF8Encoding(false, false));
        return $"The file {target} has been edited successfully.";
    }

    private static List<int> MatchOffsets(string content, string search)
    {
        var offsets = new List<int>();
        var offset = 0;
        while (true)
        {
            var match = content.IndexOf(search, offset, StringComparison.Ordinal);
            if (match < 0) return offsets;
            offsets.Add(match);
            offset = match + search.Length;
        }
    }

    private static List<int> LineNumbersAt(string content, List<int> offsets)
    {
        var line = 1;
        var cursor = 0;
        var lines = new List<int>(offsets.Count);
        foreach (var offset in offsets)
        {
            while (cursor < offset)
            {
                if (content[cursor] == '\n') line += 1;
                cursor += 1;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static JsonElement? PresentCall(JsonElement args)
    {
        if (!args.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String)
            return null;
        var command = commandElement.GetString();
        var path = args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString() ?? ""
            : "";
        var view = command switch
        {
            "view" => new JsonObject
            {
                ["card"] = "generic",
                ["title"] = $"view {path}",
                ["kind"] = "read",
                ["locations"] = new JsonArray(new JsonObject { ["path"] = path }),
            },
            "create" => new JsonObject
            {
                ["card"] = "diff",
                ["title"] = $"create {path}",
                ["diffs"] = new JsonArray(new JsonObject { ["path"] = path, ["oldText"] = null, ["newText"] = OptionalString(args, "file_text") ?? "" }),
                ["locations"] = new JsonArray(new JsonObject { ["path"] = path }),
            },
            "str_replace" => new JsonObject
            {
                ["card"] = "diff",
                ["title"] = $"str_replace {path}",
                ["diffs"] = new JsonArray(new JsonObject { ["path"] = path, ["oldText"] = OptionalString(args, "old_str"), ["newText"] = OptionalString(args, "new_str") ?? "" }),
                ["locations"] = new JsonArray(new JsonObject { ["path"] = path }),
            },
            "insert" => new JsonObject
            {
                ["card"] = "generic",
                ["title"] = $"insert {path}",
                ["kind"] = "edit",
                ["locations"] = new JsonArray(PresentInsertLocation(args, path)),
            },
            _ => null,
        };
        return view is null ? null : JsonDocument.Parse(view.ToJsonString()).RootElement;
    }

    private static JsonObject PresentInsertLocation(JsonElement args, string path)
    {
        var location = new JsonObject { ["path"] = path };
        if (OptionalInt(args, "insert_line") is { } insertLine)
            location["line"] = Math.Max(1, insertLine + 1);
        return location;
    }
}
