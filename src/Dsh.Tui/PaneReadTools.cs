using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Pty;

namespace Dsh.Tui;

/**
 * 分屏协作的只读工具: pane_list/pane_read/session_read, 让 agent 看到本 TUI 进程内其他窗格与会话的内容。
 * 注入/写入不做(已有 send_message); 输出格式 format: text(默认)/json/toon, toon 经 Dsh.Core 的 IToonCodec 契约取(未装 toon 插件时报错)。
 */
internal static class PaneReadTools
{
    public const string PaneListToolName = "pane_list";
    public const string PaneReadToolName = "pane_read";
    public const string SessionReadToolName = "session_read";
    public const string PaneSendToolName = "pane_send";

    private const int DefaultLines = 200;
    private const int MaxLines = 2000;

    private static readonly JsonObject FormatParameter = new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray("text", "json", "toon"),
        ["description"] = "Output format: `text` (default, plain lines), `json` (structured JSON), or `toon` (requires the @deepseek-ai/dsh-toon plugin).",
    };

    private static readonly JsonObject LinesParameter = new()
    {
        ["type"] = "integer",
        ["description"] = $"Number of trailing lines to return (default {DefaultLines}, max {MaxLines}).",
    };

    public static IDisposable Apply(Context ctx, ChatWindow window)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return new DisposeBundle([
            tools.Register(PaneListDefinition(ctx, window)),
            tools.Register(PaneReadDefinition(ctx, window)),
            tools.Register(SessionReadDefinition(ctx, window)),
            tools.Register(PaneSendDefinition(ctx, window)),
        ]);
    }

    private static ToolDefinition PaneListDefinition(Context ctx, ChatWindow window) => new()
    {
        Name = PaneListToolName,
        Description = "List the panes of this TUI window: id, kind (chat/shell/subagent), title, bound session or PTY, and which pane is focused. Read-only.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["format"] = FormatParameter.DeepClone(),
            },
        },
        Output = new ToolOutputDefinition(PaneListSchema, (args, value) => Render(ctx, args, value)),
        IsConcurrencySafe = _ => true,
        Execute = async (_, _) => await ListPanesAsync(window),
    };

    private static ToolDefinition PaneReadDefinition(Context ctx, ChatWindow window) => new()
    {
        Name = PaneReadToolName,
        Description = "Read the content of one pane of this TUI window (trailing lines). Chat and subagent panes give transcript lines; shell panes give the terminal screen plus scrollback. Read-only.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("paneId"),
            ["properties"] = new JsonObject
            {
                ["paneId"] = new JsonObject { ["type"] = "integer", ["description"] = "Pane id from pane_list." },
                ["lines"] = LinesParameter.DeepClone(),
                ["format"] = FormatParameter.DeepClone(),
            },
        },
        Output = new ToolOutputDefinition(PaneReadSchema, (args, value) => Render(ctx, args, value)),
        IsConcurrencySafe = _ => true,
        Execute = (args, _) => PaneReadExecute(window, args),
    };

    private static ToolDefinition SessionReadDefinition(Context ctx, ChatWindow window) => new()
    {
        Name = SessionReadToolName,
        Description = "Read the transcript of any session of this process, including background subagent sessions that have no pane (trailing lines). Read-only.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("sessionId"),
            ["properties"] = new JsonObject
            {
                ["sessionId"] = new JsonObject { ["type"] = "string", ["description"] = "Session id (from pane_list, list_agents, or /session)." },
                ["lines"] = LinesParameter.DeepClone(),
                ["format"] = FormatParameter.DeepClone(),
            },
        },
        Output = new ToolOutputDefinition(PaneReadSchema, (args, value) => Render(ctx, args, value)),
        IsConcurrencySafe = _ => true,
        Execute = (args, _) => SessionReadExecute(window, args),
    };

    private static ToolDefinition PaneSendDefinition(Context ctx, ChatWindow window) => new()
    {
        Name = PaneSendToolName,
        Description = "Send input to a pane of this TUI: `text` submits text (chat pane) or writes bytes (shell pane), `focus` switches the target TUI's focused pane. Works on local panes and on published panes of other processes behind the same daemon. Read-write.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("paneId", "kind"),
            ["properties"] = new JsonObject
            {
                ["paneId"] = new JsonObject { ["type"] = "integer", ["description"] = "Pane id from pane_list." },
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text", "focus"), ["description"] = "text: send/submit input; focus: switch focus (local panes only)." },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Payload for kind=text." },
            },
        },
        Output = new ToolOutputDefinition(PaneSendSchema, (args, value) => Render(ctx, args, value)),
        IsConcurrencySafe = _ => true,
        Execute = (args, _) => PaneSendExecute(window, args),
    };

    private static async Task<object?> ListPanesAsync(ChatWindow window)
    {
        var rows = new List<JsonNode?>();
        foreach (var entry in await window.SnapshotPanesAsync())
            rows.Add(ProjectPane(entry));
        await window.RefreshDaemonPtysAsync();
        foreach (var (ptyId, pane) in window.RemotePanes())
            rows.Add(ProjectRemotePane(ptyId, pane));
        return new JsonArray(rows.ToArray());
    }

    private static async Task<object?> PaneReadExecute(ChatWindow window, JsonElement args)
    {
        var paneId = args.GetProperty("paneId").GetInt32();
        var entry = await window.FindPaneAsync(paneId);
        if (entry is not null)
        {
            var lines = await window.SnapshotPaneLinesAsync(paneId) ?? [];
            return ReadResult(lines, RequestedLines(args), new JsonObject
            {
                ["paneId"] = entry.Id,
                ["kind"] = KindName(entry.Kind),
                ["title"] = entry.Title,
                ["remote"] = false,
            });
        }

        await window.RefreshDaemonPtysAsync();
        var remote = window.RemotePanes().FirstOrDefault(item => item.Pane.Id == paneId);
        if (remote.Pane is null)
            throw new InvalidOperationException($"pane {paneId} does not exist (run pane_list for current panes)");
        return ReadResult(remote.Pane.Lines ?? [], RequestedLines(args), new JsonObject
        {
            ["paneId"] = paneId,
            ["kind"] = remote.Pane.Kind,
            ["title"] = remote.Pane.Title,
            ["remote"] = true,
        });
    }

    private static Task<object?> SessionReadExecute(ChatWindow window, JsonElement args)
    {
        var raw = args.GetProperty("sessionId").GetString() ?? "";
        var lines = window.SnapshotSessionLines(SessionId.Create(raw))
            ?? throw new InvalidOperationException($"session not found: {raw}");
        return Task.FromResult<object?>(ReadResult(lines, RequestedLines(args), new JsonObject
        {
            ["sessionId"] = raw,
        }));
    }

    private static JsonObject ReadResult(IReadOnlyList<string> lines, int requested, JsonObject header)
    {
        var take = Math.Min(lines.Count, requested);
        header["truncated"] = lines.Count > take;
        header["lines"] = new JsonArray(lines.Skip(lines.Count - take).Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
        return header;
    }

    private static int RequestedLines(JsonElement args)
        => args.TryGetProperty("lines", out var value) && value.ValueKind == JsonValueKind.Number
            ? Math.Clamp(value.GetInt32(), 1, MaxLines)
            : DefaultLines;

    private static async Task<object?> PaneSendExecute(ChatWindow window, JsonElement args)
    {
        var paneId = args.GetProperty("paneId").GetInt32();
        var kind = args.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() ?? "text" : "text";
        var text = args.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String ? textElement.GetString() ?? "" : "";

        if (await window.FindPaneAsync(paneId) is not null)
        {
            var local = await window.DispatchAsync(() => window.ApplyPaneInput(kind, paneId, text));
            return new JsonObject { ["paneId"] = paneId, ["kind"] = kind, ["remote"] = false, ["result"] = local };
        }

        await window.RefreshDaemonPtysAsync();
        var remote = window.RemotePanes().FirstOrDefault(item => item.Pane.Id == paneId);
        if (remote.Pane is null)
            throw new InvalidOperationException($"pane {paneId} not found locally or in published remote panes (run pane_list)");
        await PtyDaemonClient.ControlSendAsync(remote.PtyId, kind, paneId, text);
        return new JsonObject { ["paneId"] = paneId, ["kind"] = kind, ["remote"] = true, ["ptyId"] = remote.PtyId, ["result"] = "queued" };
    }

    private static JsonObject ProjectRemotePane(string ptyId, PtyPaneSnapshotDto pane)
    {
        var row = new JsonObject
        {
            ["id"] = pane.Id,
            ["kind"] = pane.Kind,
            ["title"] = pane.Title,
            ["focused"] = pane.Focused,
            ["remote"] = true,
            ["ptyId"] = ptyId,
        };
        if (pane.SessionId is { Length: > 0 } sessionId)
            row["sessionId"] = sessionId;
        if (pane.Command is { Length: > 0 } command)
            row["command"] = command;
        return row;
    }

    private static JsonObject ProjectPane(ChatWindow.PaneCatalogEntry entry)
    {
        var row = new JsonObject
        {
            ["id"] = entry.Id,
            ["kind"] = KindName(entry.Kind),
            ["title"] = entry.Title,
            ["focused"] = entry.Focused,
        };
        if (entry.SessionId is { } sessionId)
            row["sessionId"] = sessionId;
        if (entry.PtyId is { } ptyId)
            row["ptyId"] = ptyId;
        if (entry.PtyCommand is { } command)
            row["command"] = command;
        if (entry.Kind == TuiPaneKind.Shell)
            row["exited"] = entry.Exited;
        return row;
    }

    private static string KindName(TuiPaneKind kind) => kind switch
    {
        TuiPaneKind.Chat => "chat",
        TuiPaneKind.Subagent => "subagent",
        _ => "shell",
    };

    /** 按 format 渲染输出值: text 给纯文本行, json 给结构化原文, toon 经 IToonCodec 契约编码(插件缺失在请求时就报错)。 */
    private static IReadOnlyList<ContentBlock> Render(Context ctx, JsonElement args, JsonElement value)
    {
        var format = args.TryGetProperty("format", out var raw) && raw.ValueKind == JsonValueKind.String
            ? raw.GetString() ?? "text"
            : "text";
        switch (format)
        {
            case "json":
                return [new TextBlock(value.GetRawText())];
            case "toon":
                var codec = ctx.Get<IToonCodec>(IToonCodec.ServiceName, false)
                    ?? throw new InvalidOperationException("format \"toon\" requires the @deepseek-ai/dsh-toon plugin (it provides the toon-codec service)");
                return [new TextBlock(codec.Encode(JsonNode.Parse(value.GetRawText())))];
            default:
                return [new TextBlock(RenderText(value))];
        }
    }

    private static string RenderText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return RenderPaneListText(value);
        if (value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            return result.GetString() ?? "";
        IEnumerable<string> lines = value.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(line => line.GetString() ?? "")
            : [];
        var text = string.Join('\n', lines);
        if (value.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            text = $"(truncated to last {RequestedLinesNote(value)} lines)\n{text}";
        return text;
    }

    private static int RequestedLinesNote(JsonElement value)
        => value.TryGetProperty("lines", out var array) ? array.GetArrayLength() : 0;

    private static string RenderPaneListText(JsonElement value)
    {
        if (value.GetArrayLength() == 0)
            return "(no panes)";
        var lines = new List<string>();
        foreach (var row in value.EnumerateArray())
        {
            var line = $"pane {row.GetProperty("id").GetInt32()} [{row.GetProperty("kind").GetString()}] {row.GetProperty("title").GetString()}";
            if (row.TryGetProperty("sessionId", out var sessionId))
                line += $" session={sessionId.GetString()}";
            if (row.TryGetProperty("ptyId", out var ptyId))
                line += $" pty={ptyId.GetString()}";
            if (row.GetProperty("focused").GetBoolean())
                line += " (focused)";
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    private static readonly JsonObject PaneListSchema = new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id", "kind", "title", "focused"),
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "integer" },
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("chat", "subagent", "shell") },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["sessionId"] = new JsonObject { ["type"] = "string" },
                ["ptyId"] = new JsonObject { ["type"] = "string" },
                ["command"] = new JsonObject { ["type"] = "string" },
                ["focused"] = new JsonObject { ["type"] = "boolean" },
                ["exited"] = new JsonObject { ["type"] = "boolean" },
                ["remote"] = new JsonObject { ["type"] = "boolean" },
            },
        },
    };

    private static readonly JsonObject PaneReadSchema = new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("truncated", "lines"),
        ["properties"] = new JsonObject
        {
            ["paneId"] = new JsonObject { ["type"] = "integer" },
            ["kind"] = new JsonObject { ["type"] = "string" },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["sessionId"] = new JsonObject { ["type"] = "string" },
            ["remote"] = new JsonObject { ["type"] = "boolean" },
            ["truncated"] = new JsonObject { ["type"] = "boolean" },
            ["lines"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
    };

    private static readonly JsonObject PaneSendSchema = new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("paneId", "kind", "remote", "result"),
        ["properties"] = new JsonObject
        {
            ["paneId"] = new JsonObject { ["type"] = "integer" },
            ["kind"] = new JsonObject { ["type"] = "string" },
            ["remote"] = new JsonObject { ["type"] = "boolean" },
            ["ptyId"] = new JsonObject { ["type"] = "string" },
            ["result"] = new JsonObject { ["type"] = "string" },
        },
    };

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
