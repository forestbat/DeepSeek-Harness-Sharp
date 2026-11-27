using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Dsh.Tests;

/**
 * 测试进程也要像生产进程一样把各插件程序集的启动登记跑一遍。
 *
 * 会话事件编解码是一张进程级静态注册表, 由各模块的 [DshPluginInitializer] 在插件启动时登记;
 * 测试进程过去只有"恰好先跑了某个会组合插件的用例"时才有这些类型, 于是能否读某份日志取决于执行顺序。
 * 这里在每个 Dsh.* 程序集加载时补跑它自己的生成 bootstrap, 让整个测试进程的编解码状态与生产一致。
 */
internal static class PluginCodecInitializer
{
    private static readonly ConcurrentDictionary<string, bool> Registered = new(StringComparer.Ordinal);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (typeof(PluginCodecInitializer).Assembly.IsCollectible)
            return;
        AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) => Register(loaded.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            Register(assembly);
    }

    private static void Register(Assembly assembly)
    {
        if (assembly.IsCollectible)
            return;
        var name = assembly.GetName().Name;
        if (name is null || !name.StartsWith("Dsh.", StringComparison.Ordinal) || name == "Dsh.Tests")
            return;
        if (!Registered.TryAdd(name, true))
            return;
        var bootstrap = assembly.GetType($"{name}.Generated.DshPluginBootstrap", throwOnError: false);
        if (bootstrap is null) return;
        bootstrap.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
    }
}