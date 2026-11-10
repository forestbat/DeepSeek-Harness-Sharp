using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Dsh.Plugins.Native;

/**
 * Native plugin ABI v1 exchanges C calling-convention function tables and UTF-8 strings.
 * Input strings are NUL-terminated; two-phase outputs carry their length without a terminator.
 */
public static class DshNativePluginAbi
{
    public const int Version = 1;
    public const string EntryPoint = "dsh_plugin_entry";
    public const string SizedEntryPoint = "dsh_plugin_entry_with_host_size";
    public const string PackageExport = "dsh_plugin_package";
    /** 可选的独立版本导出:插件声明自身 ABI 版本,使宿主在握手前就能精确区分“版本不符”与“其它拒绝”。 */
    public const string VersionExport = "dsh_plugin_abi_version";
    /** 可选的配置面导出:插件声明自己认哪些参数(JSON),宿主据此把参数传下去并纳入文档。 */
    public const string SettingsSchemaExport = "dsh_plugin_settings_schema";
    /** 配置回传缓冲上限:超出即视为插件不认这份配置,宿主只记 WARN(避免无界重试)。 */
    public const int ConfigCapacityHint = 64 * 1024;
    public const int MaxResultBytes = PluginHostOperations.MaximumFrameBytes;

    public const int Ok = 0;
    public const int Error = -1;
    public const int LogDebug = 0;
    public const int LogInfo = 1;
    public const int LogWarn = 2;
    public const int LogError = 3;
}

/** 宿主侧握手探测结果:非静默诊断「缺导出符号 / ABI 版本不符 / 被拒」。 */
public enum DshNativeHandshake
{
    Ok = 0,
    MissingExports = 1,
    VersionMismatch = 2,
    Rejected = 3,
}

/** 标注函数表字段在协议中的角色:两侧胶水由生成器按角色产出,不靠字段名约定。 */
[AttributeUsage(AttributeTargets.Field)]
public sealed class DshAbiRoleAttribute(string role) : Attribute
{
    public string Role { get; } = role;
}

/** Roles consumed by both sides of the ABI generator. */
public static class DshAbiRoles
{
    public const string Package = "package";
    public const string Activate = "activate";
    public const string Deactivate = "deactivate";
    public const string InvokeTool = "invoke_tool";
    public const string Log = "log";
    public const string RegisterTool = "register_tool";
    /** Removes a registered tool by handle. */
    public const string UnregisterTool = "unregister_tool";
    /** Reads this plugin's JSON configuration through a two-phase buffer. */
    public const string GetConfig = "get_config";
    public const string HostCall = "host_call";
}

/** 宿主提供给插件的函数表;Context 是不透明句柄,插件只负责原样回传。 */
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DshHostApi
{
    public int AbiVersion;
    public void* Context;
    [DshAbiRole(DshAbiRoles.Log)]
    public delegate* unmanaged[Cdecl]<void*, int, byte*, void> Log;
    [DshAbiRole(DshAbiRoles.RegisterTool)]
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, byte*, int, int> RegisterTool;
    /** Removes a tool registered by this activation. */
    [DshAbiRole(DshAbiRoles.UnregisterTool)]
    public delegate* unmanaged[Cdecl]<void*, int, int> UnregisterTool;
    /** Reads plugin configuration; null output probes the required UTF-8 byte count. */
    [DshAbiRole(DshAbiRoles.GetConfig)]
    public delegate* unmanaged[Cdecl]<void*, byte*, int, int> GetConfig;
    /** Optional tail negotiated by the sized entry export; one caller-owned frame, no probes or retries. */
    [DshAbiRole(DshAbiRoles.HostCall)]
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, byte*, int, int> HostCall;
}

/** 插件提供给宿主的函数表;Package 指向插件内静态 UTF-8 缓冲区,插件卸载前一直有效。 */
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DshPluginApi
{
    public int AbiVersion;
    [DshAbiRole(DshAbiRoles.Package)]
    public byte* Package;
    [DshAbiRole(DshAbiRoles.Activate)]
    public delegate* unmanaged[Cdecl]<void*, int> Activate;
    [DshAbiRole(DshAbiRoles.Deactivate)]
    public delegate* unmanaged[Cdecl]<void*, void> Deactivate;
    [DshAbiRole(DshAbiRoles.InvokeTool)]
    public delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, int, int> InvokeTool;
}

/** 插件侧运行期工具登记入口:生成器在激活时挂上,作者可随时增删工具。 */
public interface INativeToolRegistry
{
    /** 登记一个工具,返回宿主句柄(供之后 UnregisterTool);失败返回 0。 */
    int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke);

    /** 撤销一个已登记工具;返回 DshNativePluginAbi.Ok / Error。 */
    int UnregisterTool(int handle);
}

/** 插件侧工具注册与调度的便利包装:插件在 Activate 里注册工具,由 Dispatch 派发调用。 */
public sealed unsafe class NativePluginContext(DshHostApi* host, void* context, int hostApiSize) : INativeToolRegistry, IDisposable
{
    private readonly Dictionary<int, Func<string, string?>> _handlers = [];
    private int _nextHandle;

    [ThreadStatic]
    private static RetainedToolResult? _retained;

    public void Log(int level, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        fixed (byte* pointer = NativeUtf8.Terminated(message))
            host->Log(context, level, pointer);
    }

    public int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handle = Interlocked.Increment(ref _nextHandle);
        _handlers[handle] = invoke;
        int result;
        fixed (byte* namePointer = NativeUtf8.Terminated(name))
        fixed (byte* descriptionPointer = NativeUtf8.Terminated(description))
        fixed (byte* parametersPointer = NativeUtf8.Terminated(parametersJson))
            result = host->RegisterTool(context, namePointer, descriptionPointer, parametersPointer, handle);
        if (result != DshNativePluginAbi.Ok)
        {
            _handlers.Remove(handle);
            return 0;
        }
        return handle;
    }

    public int UnregisterTool(int handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (host->UnregisterTool is null)
            return DshNativePluginAbi.Error;
        var result = host->UnregisterTool(context, handle);
        if (result == DshNativePluginAbi.Ok)
            _handlers.Remove(handle);
        return result;
    }

    public string? Dispatch(int handle, string inputJson)
        => _handlers.TryGetValue(handle, out var invoke) ? invoke(inputJson) : null;

    /// <summary>Preserves the v1 tool-output retry contract for older hosts.</summary>
    public int InvokeTool(int handle, byte* input, byte* output, int capacity)
    {
        if (capacity < 0)
            return DshNativePluginAbi.Error;
        var inputJson = NativeUtf8.Read(input);
        var retained = _retained;
        _retained = null;
        var matches = retained is { Owner: var owner } && ReferenceEquals(owner, this)
            && retained.Handle == handle && retained.Input == inputJson;
        var result = matches ? retained!.Result : Dispatch(handle, inputJson) ?? "";
        var length = Encoding.UTF8.GetByteCount(result);
        if (length > DshNativePluginAbi.MaxResultBytes)
            return DshNativePluginAbi.Error;
        if (output is null || capacity < length)
        {
            _retained = matches ? retained : new RetainedToolResult(this, handle, inputJson, result);
            return length;
        }
        return Encoding.UTF8.GetBytes(result, new Span<byte>(output, capacity));
    }

    private sealed record RetainedToolResult(NativePluginContext Owner, int Handle, string Input, string Result);

    public bool HasHostCall => hostApiSize >= sizeof(DshHostApi) && host->HostCall is not null;

    private bool _disposed;

    public string CallHost(string operation, string argumentsJson)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasHostCall)
            throw new InvalidOperationException("the host does not provide native host calls");
        var buffer = new byte[DshNativePluginAbi.MaxResultBytes];
        fixed (byte* operationPointer = NativeUtf8.Terminated(operation))
        fixed (byte* argumentsPointer = NativeUtf8.Terminated(argumentsJson))
        {
            int written;
            fixed (byte* output = buffer)
                written = host->HostCall(context, operationPointer, argumentsPointer, output, buffer.Length);
            if (written <= 0 || written > buffer.Length)
                throw new InvalidOperationException($"host operation '{operation}' failed its native buffer exchange");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, written));
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"host operation '{operation}' failed: {error.GetString()}");
            return document.RootElement.GetProperty("result").GetRawText();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(_retained?.Owner, this))
            _retained = null;
        _handlers.Clear();
    }

    /** This plugin's JSON configuration; an absent configuration is an empty string. */
    public string ConfigJson()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (host->GetConfig is null)
            return "";
        var required = host->GetConfig(context, null, 0);
        if (required == 0)
            return "";
        if (required < 0 || required > DshNativePluginAbi.ConfigCapacityHint)
            throw new InvalidOperationException("native plugin configuration exceeds its buffer limit or failed");
        var buffer = new byte[required];
        fixed (byte* pointer = buffer)
        {
            var written = host->GetConfig(context, pointer, required);
            if (written < 0 || written > buffer.Length)
                throw new InvalidOperationException("native plugin configuration changed or failed during buffer exchange");
            return Encoding.UTF8.GetString(buffer, 0, written);
        }
    }
}

/** 插件侧 UTF-8 互操作助手。 */
public static unsafe class NativeUtf8
{
    public static string Read(byte* pointer)
    {
        if (pointer is null)
            return "";
        var length = 0;
        while (pointer[length] != 0)
            length++;
        return Encoding.UTF8.GetString(pointer, length);
    }

    /** 以指针传给对端的字符串:UTF-8 并补 NUL 终止符,对端按终止符读取。 */
    public static byte[] Terminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    /** 常驻字符串(如包名):分配一次并固定,供导出函数返回稳定指针。 */
    public static byte* Persistent(string value)
        => (byte*)Marshal.StringToCoTaskMemUTF8(value);

    /** 两段式缓冲协议:output 为 null 或容量不足时只返回所需字节数,否则写出内容并返回写入长度。 */
    public static int Write(string? value, byte* output, int capacity)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (output is null || capacity < bytes.Length)
            return bytes.Length;
        for (var index = 0; index < bytes.Length; index++)
            output[index] = bytes[index];
        return bytes.Length;
    }
}
