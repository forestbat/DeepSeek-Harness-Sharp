using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Plugins;

namespace Dsh.Plugins.Sandbox;

/** Wire values carry data only; shared-process plugins remain co-trusted. */
internal static class PluginSandboxMethods
{
    internal const int Version = 1;
    internal const string Ready = "sandbox/ready";
    internal const string Inspect = "sandbox/inspect";
    internal const string Load = "sandbox/load";
    internal const string Unload = "sandbox/unload";
    internal const string Invoke = "sandbox/invoke";
    internal const string RegisterTool = "sandbox/registerTool";
    internal const string UnregisterTool = "sandbox/unregisterTool";
    internal const string HostCall = "sandbox/hostCall";
    internal const string Log = "sandbox/log";
    internal const int MaximumFrameBytes = PluginHostOperations.MaximumFrameBytes;
    internal const int MaximumTools = PluginHostOperations.MaximumTools;
    internal const int MaximumPendingRequests = 16;
    internal const int MaximumPackagesPerArtifact = 256;
    internal const int MaximumPackageCharacters = 256;
    internal const int MaximumFailureCharacters = 2048;
}

public sealed record PluginSandboxReady(int Version, int Pid);
public sealed record PluginSandboxInspectRequest(string? ArtifactPath, string? Package = null);
public sealed record PluginSandboxMetadata(string Package, PluginForm Form, string? Entry, bool SupportsCapabilities);
public sealed record PluginSandboxInspection(PluginSandboxMetadata[] Packages, string[] Errors, bool Released = true);
public sealed record PluginSandboxLoadRequest(string? ArtifactPath, string Package, JsonElement Configuration);
public sealed record PluginSandboxUnloadRequest(string Package);
public sealed record PluginSandboxInvokeRequest(string Package, int ToolId, JsonElement Arguments, string Invocation);
public sealed record PluginSandboxToolRequest(string Package, int ToolId, string Name, string Description, JsonElement Parameters);
public sealed record PluginSandboxUnregisterTool(string Package, int ToolId);
public sealed record PluginSandboxHostCall(string Package, string? Invocation, string Operation, JsonElement Arguments);
public sealed record PluginSandboxLog(string Package, int Level, string Message);
public sealed record PluginSandboxAck(bool Success = true);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginSandboxReady))]
[JsonSerializable(typeof(PluginSandboxInspectRequest))]
[JsonSerializable(typeof(PluginSandboxInspection))]
[JsonSerializable(typeof(PluginSandboxLoadRequest))]
[JsonSerializable(typeof(PluginSandboxUnloadRequest))]
[JsonSerializable(typeof(PluginSandboxInvokeRequest))]
[JsonSerializable(typeof(PluginSandboxToolRequest))]
[JsonSerializable(typeof(PluginSandboxUnregisterTool))]
[JsonSerializable(typeof(PluginSandboxHostCall))]
[JsonSerializable(typeof(PluginSandboxLog))]
[JsonSerializable(typeof(PluginSandboxAck))]
// JSON-RPC 外壳本身也要这两样: 错误数据与原始 JSON 载荷, 缺了会让应答序列化失败并把连接带走。
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
public sealed partial class PluginSandboxJsonContext : JsonSerializerContext;
