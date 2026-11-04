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
}

internal sealed record PluginSandboxSource(PluginDescriptor Descriptor, string? ArtifactPath);
internal sealed record PluginSandboxReady(int Version, int Pid);
internal sealed record PluginSandboxInspectRequest(string? ArtifactPath, string? Package = null);
internal sealed record PluginSandboxMetadata(string Package, PluginForm Form, string? Entry, bool SupportsCapabilities);
internal sealed record PluginSandboxInspection(PluginSandboxMetadata[] Packages, string[] Errors, bool Released = true);
internal sealed record PluginSandboxLoadRequest(string? ArtifactPath, string Package, JsonElement Configuration);
internal sealed record PluginSandboxUnloadRequest(string Package);
internal sealed record PluginSandboxInvokeRequest(string Package, int ToolId, JsonElement Arguments, string Invocation);
internal sealed record PluginSandboxToolRequest(string Package, int ToolId, string Name, string Description, JsonElement Parameters);
internal sealed record PluginSandboxUnregisterTool(string Package, int ToolId);
internal sealed record PluginSandboxHostCall(string Package, string? Invocation, string Operation, JsonElement Arguments);
internal sealed record PluginSandboxLog(string Package, int Level, string Message);
internal sealed record PluginSandboxAck(bool Success = true);

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
internal sealed partial class PluginSandboxJsonContext : JsonSerializerContext;
