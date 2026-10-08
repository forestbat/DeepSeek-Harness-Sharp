using System.Runtime.InteropServices;
using Dsh.Plugins.Generated;
using Dsh.Plugins.Native;

namespace Dsh.Plugins;

/** 原生插件装载失败的结构化原因:不再静默吞掉,供启动 WARN、/plugins list 失败分组与 doctor 使用。 */
public enum NativePluginFailureKind
{
    /** 不是可加载的原生共享库(非原生 AOT 产物 / 依赖缺失 / 非 NativeLib=Shared)。 */
    NotALibrary,

    /** 缺 dsh_plugin_package / dsh_plugin_entry 导出符号。 */
    MissingExports,

    /** ABI 版本与宿主不一致(插件导出了独立版本号时精确判定)。 */
    AbiVersionMismatch,

    /** 导出齐全但握手被插件拒绝。 */
    HandshakeRejected,

    /** 包名已被登记(compiled-in / 已装载的同名插件):拒绝重复登记。 */
    DuplicatePackage,

    /** 此前同类故障累计到阈值,已被 quarantine 禁装。 */
    Quarantined,

    /** 装载/握手过程中抛出异常。 */
    LoadFault,
}

/** 一次原生装载的结果:成功持有插件,失败给出结构化原因与可读消息。 */
public sealed record NativePluginLoad(
    NativePluginLibrary? Plugin,
    NativePluginFailureKind? Failure,
    string? Reason)
{
    public bool Succeeded => Plugin is not null;

    public static NativePluginLoad Ok(NativePluginLibrary plugin) => new(plugin, null, null);

    public static NativePluginLoad Fail(NativePluginFailureKind kind, string reason) => new(null, kind, reason);
}

/** 原生插件在托管侧的能力回调:宿主在插件 Activate 时提供。
 *  工具的登记/反注册都由句柄寻址——插件侧生成句柄,宿主按句柄增删,热增删立即反映到 ToolRuntime。 */
public interface INativePluginHost
{
    void Log(int level, string message);

    void RegisterTool(int handle, string name, string description, string parametersJson, Func<string, string?> invoke);

    /** 撤销一个已登记工具;句柄不存在返回 false。 */
    bool UnregisterTool(int handle);
}

/** 原生插件句柄:包名、生命周期、故障状态与工具调用。 */
public interface INativePlugin : IDisposable
{
    string Package { get; }

    NativePluginState State { get; }

    void Activate(INativePluginHost host);

    void Deactivate();

    /** 宿主在捕获到该插件的一次故障后调用:标记 Faulted 并累计 quarantine。 */
    void RecordFault(string reason);
}

/** 原生共享库插件:NativeLibrary.Load + 生成胶水的握手、句柄与两段式调用。
 *  每个实例持有一个 NativePluginLoadContext(句柄 + 导出绑定 + 故障状态),与托管插件的 ALC 对称。 */
public sealed unsafe class NativePluginLibrary : INativePlugin
{
    private static readonly Sink Dispatcher = new();

    private readonly NativePluginLoadContext _context;
    private INativePluginHost? _host;
    private GCHandle _self;

    static NativePluginLibrary() => DshNativeHostApi.Calls = Dispatcher;

    private NativePluginLibrary(NativePluginLoadContext context)
    {
        _context = context;
        Package = context.Package;
    }

    public string Package { get; }

    public NativePluginState State => _context.State;

    public NativePluginLoadContext Context => _context;

    /** 按结构化原因装载原生插件:缺导出 / ABI 版本不符 / 非共享库 / quarantine 都给出明确原因。 */
    public static NativePluginLoad TryLoad(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (NativePluginQuarantine.Default.IsBanned(fullPath))
            return NativePluginLoad.Fail(NativePluginFailureKind.Quarantined,
                $"该原生插件此前连续多次故障,已被 quarantine 禁止加载: {fullPath}");
        nint library;
        try
        {
            if (!NativeLibrary.TryLoad(fullPath, out library))
                return NativePluginLoad.Fail(NativePluginFailureKind.NotALibrary,
                    $"不是可加载的原生共享库(非 NativeLib=Shared 产物或依赖缺失): {fullPath}");
        }
        catch (Exception error)
        {
            return NativePluginLoad.Fail(NativePluginFailureKind.NotALibrary,
                $"原生库装载抛异常: {error.Message}");
        }
        try
        {
            var status = DshPluginApiBinding.TryHandshake(library, out var api);
            if (status != DshNativeHandshake.Ok)
            {
                NativeLibrary.Free(library);
                return NativePluginLoad.Fail(Map(status), Describe(status, fullPath));
            }
            var binding = new DshPluginApiBinding(api);
            var context = new NativePluginLoadContext(library, binding, fullPath, NativeUtf8.Read(api.Package));
            return NativePluginLoad.Ok(new NativePluginLibrary(context));
        }
        catch (Exception error)
        {
            NativeLibrary.Free(library);
            return NativePluginLoad.Fail(NativePluginFailureKind.LoadFault,
                $"原生插件握手抛异常: {error.Message}");
        }
    }

    private static NativePluginFailureKind Map(DshNativeHandshake status) => status switch
    {
        DshNativeHandshake.MissingExports => NativePluginFailureKind.MissingExports,
        DshNativeHandshake.VersionMismatch => NativePluginFailureKind.AbiVersionMismatch,
        _ => NativePluginFailureKind.HandshakeRejected,
    };

    private static string Describe(DshNativeHandshake status, string path) => status switch
    {
        DshNativeHandshake.MissingExports =>
            $"缺少导出符号 {DshNativePluginAbi.PackageExport}/{DshNativePluginAbi.EntryPoint}: {path}",
        DshNativeHandshake.VersionMismatch =>
            $"ABI 版本不符(宿主 v{DshNativePluginAbi.Version}): {path}",
        _ => $"原生插件拒绝握手: {path}",
    };

    public void Activate(INativePluginHost host)
    {
        _host = host;
        _self = GCHandle.Alloc(this);
        if (_context.Activate(GCHandle.ToIntPtr(_self)) != DshNativePluginAbi.Ok)
        {
            _self.Free();
            _host = null;
            throw new InvalidOperationException($"native plugin \"{Package}\" refused activation");
        }
        _context.MarkActive();
    }

    public void Deactivate()
    {
        if (_host is null)
            return;
        _context.Deactivate(GCHandle.ToIntPtr(_self));
        _self.Free();
        _host = null;
    }

    public void RecordFault(string reason)
    {
        var count = _context.RecordFault(reason);
        _host?.Log(DshNativePluginAbi.LogError,
            $"native plugin \"{Package}\" faulted: {reason}; fault count {count}"
            + (count >= NativePluginQuarantine.BanThreshold ? ", quarantined and refused on next load" : ""));
    }

    public void Dispose()
    {
        Deactivate();
        _context.Dispose();
    }

    private string? InvokeTool(int handle, string inputJson)
        => _context.InvokeTool(GCHandle.ToIntPtr(_self), handle, inputJson);

    private static NativePluginLibrary? FromHandle(nint context)
        => context == 0 ? null : (NativePluginLibrary?)GCHandle.FromIntPtr(context).Target;

    /** 生成蹦床的落地:从 ABI 上下文句柄找回插件实例,再走托管回调。 */
    private sealed class Sink : IDshNativeHostCalls
    {
        public void Log(nint context, int level, string message)
            => FromHandle(context)?._host?.Log(level, message);

        public int RegisterTool(nint context, string name, string description, string parametersJson, int handle)
        {
            var plugin = FromHandle(context);
            if (plugin?._host is null)
                return DshNativePluginAbi.Error;
            plugin._host.RegisterTool(handle, name, description, parametersJson, input => plugin.InvokeTool(handle, input));
            return DshNativePluginAbi.Ok;
        }

        public int UnregisterTool(nint context, int handle)
        {
            var plugin = FromHandle(context);
            if (plugin?._host is null)
                return DshNativePluginAbi.Error;
            return plugin._host.UnregisterTool(handle) ? DshNativePluginAbi.Ok : DshNativePluginAbi.Error;
        }
    }
}
