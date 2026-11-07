using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Inspection;

/**
 * settings_set: 结构化写 settings.yaml 的唯一正路。模型给"路径 + JSON 值", 由 SettingsDocument 渲染 YAML 并原子替换,
 * 因此写不出语法错误, 注释与其它段逐字节保留。
 */
public static class ToolSettings
{
    public const string ToolName = "settings_set";
    public const string HomeProperty = "harnessOptions";

    internal const string ApprovalReason =
        "settings_set 修改 dsharp 的配置文件 settings.yaml; 批准后按给定的路径写入一条值。";

    private const string Description =
        "Change one settings.yaml key by path instead of editing the file. Give \"path\" (dot separated, a quoted "
        + "segment such as plugins.\"@deepseek-ai/dsh-tui\".keys.newSession when the name contains a dot) and \"value\" "
        + "as a JSON literal (string, number, boolean, array or object). Use mode \"delete\" to remove the key. "
        + "Unknown keys are rejected with the closest known paths. Comments and other sections of the file are preserved.";

    /** 装配类段改动需要重启或各自的 reload 命令, 纯数据段下一次读取即生效。 */
    private static readonly string[] RestartSections = ["providers", "plugins", "mcp", "a2a"];

    internal static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return new DisposableBundle(
            tools.Register(Definition(ctx)),
            ApprovalGate(ctx),
            prompt.Section(PromptSection.Literal(
                "tool:settings_set",
                "Use settings_set to change dsharp's own settings.yaml. Never overwrite that file with write or edit: "
                + "the tool keeps comments, validates the whole document and rejects unknown keys.")));
    }

    private static IDisposable ApprovalGate(Context ctx)
    {
        var gate = ctx.OnWaterfall<ToolPreExecuteNotification>(
            (notification, next) => string.Equals(notification.Run.Name, ToolName, StringComparison.Ordinal)
                ? ValueTask.FromResult<object?>(new PreToolDecision.Ask(ApprovalReason))
                : next(),
            new EventOptions { Global = true });
        return new ActionDisposable(() => gate());
    }

    private static ToolDefinition Definition(Context ctx) => new()
    {
        Name = ToolName,
        Description = Description,
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["path"],
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Dot separated settings path, e.g. logging.level or mcp.phone.url."
                },
                "value": {
                  "description": "JSON literal to write (string, number, boolean, array or object). Omit for mode delete."
                },
                "mode": {
                  "type": "string",
                  "enum": ["set", "delete"],
                  "description": "set (default) writes value; delete removes the key."
                }
              }
            }
            """)!.AsObject(),
        Output = new ToolOutputDefinition(
            JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["path", "mode", "result"],
                  "properties": {
                    "path": { "type": "string" },
                    "mode": { "type": "string" },
                    "result": { "type": "string" }
                  }
                }
                """)!.AsObject(),
            (_, value) => [new TextBlock(value.GetProperty("result").GetString() ?? "")]),
        Execute = (args, _) => Task.FromResult<object?>(Set(ctx, args)),
    };

    private static object Set(Context ctx, JsonElement args)
    {
        var path = RequirePath(args);
        var mode = ModeOf(args);
        var segments = SettingsPath.Split(path);
        EnsureKnownPath(segments, path);
        var home = (ctx.GetProp(HomeProperty) as HarnessOptions)?.Home
            ?? throw new InvalidOperationException("settings_set needs the harness home");
        HarnessSettings.SetEntry(home, segments, mode == "delete" ? null : ValueOf(args));
        return new JsonObject
        {
            ["path"] = path,
            ["mode"] = mode,
            ["result"] = $"{(mode == "delete" ? "removed" : "written")}: {path}{Effect(segments[0])}",
        };
    }

    private static string Effect(string section)
        => RestartSections.Contains(section, StringComparer.Ordinal)
            ? " (takes effect after restart or that plugin's reload)"
            : " (takes effect on the next settings read)";

    private static string ModeOf(JsonElement args)
    {
        var mode = args.TryGetProperty("mode", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : "set";
        if (mode is not ("set" or "delete"))
            throw new InvalidOperationException("settings_set mode must be \"set\" or \"delete\"");
        if (mode == "set" && !args.TryGetProperty("value", out _))
            throw new InvalidOperationException("settings_set requires \"value\" unless mode is \"delete\"");
        return mode;
    }

    private static string RequirePath(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (path is null || path.Trim().Length == 0)
            throw new InvalidOperationException("settings_set requires a non-empty \"path\"");
        return path.Trim();
    }

    /** 路径必须命中 schema 里登记过的键: 否则会写进文件却在启动时被静默忽略。 */
    private static void EnsureKnownPath(IReadOnlyList<string> segments, string path)
    {
        var known = InspectCatalog.ConfigPaths();
        if (known.Any(entry => string.Equals(entry.Path, path, StringComparison.Ordinal)) || KnownWithoutDynamicSegment(known, segments))
            return;
        var near = known.Where(entry => entry.Path.Contains(path, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Path)
            .Take(5)
            .ToArray();
        throw new InvalidOperationException(
            near.Length == 0
                ? $"unknown settings path \"{path}\"; use creative_inspect_list category \"config\" to see every key"
                : $"unknown settings path \"{path}\"; closest known keys: {string.Join(", ", near)}");
    }

    /**
     * 字典段(mcp / providers / a2a.remotes)的键是用户起的名字, schema 只登记了值类型的属性,
     * 所以去掉这一段再匹配一次: mcp.phone.url 对应 schema 里的 mcp.url。
     */
    private static bool KnownWithoutDynamicSegment(List<(string Path, PluginSettingSpec Spec)> known, IReadOnlyList<string> segments)
    {
        for (var index = 1; index < segments.Count - 1; index++)
        {
            var candidate = string.Join('.', segments.Where((_, position) => position != index));
            if (known.Any(entry => string.Equals(entry.Path, candidate, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static object? ValueOf(JsonElement args)
        => args.TryGetProperty("value", out var value) ? FromJson(value) : null;

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => value.EnumerateObject()
            .ToDictionary(property => property.Name, property => FromJson(property.Value), StringComparer.Ordinal),
        _ => throw new InvalidOperationException($"settings_set cannot write a {value.ValueKind} value"),
    };
}

/** settings 路径的语法: 点分段, 双引号段内允许点和斜杠(包名形如 \"@deepseek-ai/dsh-tui\")。 */
internal static class SettingsPath
{
    private const char Separator = '.';
    private const char Quote = '"';

    public static IReadOnlyList<string> Split(string path)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in path)
        {
            if (character == Quote)
            {
                quoted = !quoted;
                continue;
            }
            if (character == Separator && !quoted)
            {
                segments.Add(Take(current, path));
                continue;
            }
            current.Append(character);
        }
        segments.Add(Take(current, path));
        return segments;
    }

    private static string Take(System.Text.StringBuilder segment, string path)
    {
        var text = segment.ToString().Trim();
        segment.Clear();
        return text.Length == 0
            ? throw new InvalidOperationException($"settings path \"{path}\" has an empty segment")
            : text;
    }
}
