using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Plugins;
using Dsh.Plugins.Native;

namespace Dsh.NativePluginSample;

/** 声明式原生插件样例:只声明包名与工具;
 *  导出握手、句柄表、两段式缓冲协议与异常拦截由生成器产出(见 DshNativePluginExports)。 */
public sealed class SamplePlugin : IDshNativePlugin
{
    private const string EchoSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["message"],
          "properties": { "message": { "type": "string", "description": "Text to echo" } }
        }
        """;

    private const string LengthSchema = """
        {
          "type": "object",
          "properties": { "message": { "type": "string", "description": "Text to measure" } }
        }
        """;

    private const string ReadSchema = """
        {"type":"object","required":["path"],"properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":0},"count":{"type":"integer","minimum":0}},"additionalProperties":false}
        """;
    private const string WriteSchema = """
        {"type":"object","required":["path","data"],"properties":{"path":{"type":"string"},"data":{"type":"string"},"append":{"type":"boolean"}},"additionalProperties":false}
        """;
    private const string HttpSchema = """
        {"type":"object","required":["url"],"properties":{"url":{"type":"string"},"method":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"body":{"type":"string"}},"additionalProperties":false}
        """;

    private static readonly NativePluginTool[] RegisteredTools =
    [
        new("native_echo", "Echo the given message back (native ABI sample).", EchoSchema, Echo),
        new("native_length", "Return the length of the given message (native ABI sample).", LengthSchema, Length),
        new("native_read_file", "Read a bounded file chunk through the host capability.", ReadSchema,
            input => DshNativePluginRuntime.CallHost(PluginHostOperations.ReadFile, input)),
        new("native_write_file", "Write base64 bytes through the host capability.", WriteSchema,
            input => DshNativePluginRuntime.CallHost(PluginHostOperations.WriteFile, input)),
        new("native_http", "Send HTTP through the host network capability.", HttpSchema,
            input => DshNativePluginRuntime.CallHost(PluginHostOperations.SendHttp, input)),
        new("native_config", "Return this activation's JSON configuration and owning PID namespace.", """{"type":"object","additionalProperties":false}""",
            _ => new JsonObject { ["config"] = DshNativePluginRuntime.Config, ["pidNamespace"] = OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/pid").LinkTarget : null }.ToJsonString()),
    ];

    public string Package => "sample.native-echo";

    public IReadOnlyList<NativePluginTool> Tools => RegisteredTools;

    private static string? Echo(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() ?? "" : "";
        DshNativePluginRuntime.Log(DshNativePluginAbi.LogInfo, $"echo invoked: {message}");
        return new JsonObject { ["text"] = $"echo:{message}" }.ToJsonString();
    }

    /** 缺 message 时在插件代码里触发空引用: 运行时把它变成异常, 宿主只把这次调用记为工具错误, 插件继续可用。 */
    private static string? Length(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
        return new JsonObject { ["text"] = $"length:{message!.Length}" }.ToJsonString();
    }
}
