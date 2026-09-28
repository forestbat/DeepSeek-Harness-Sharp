using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Dsh.Plugins;

/** 协作式卸载验证:发起 Unload() 并返回弱引用;调用方必须先丢弃全部强引用,再调用 WaitForCollection。 */
public static class PluginUnloader
{
    public const int MaxGcRounds = 10;

    public static WeakReference Unload(AssemblyLoadContext context)
    {
        var weak = new WeakReference(context, trackResurrection: true);
        context.Unload();
        return weak;
    }

    /**
     * 等待 ALC 被回收。maxRounds 默认 MaxGcRounds(生产报告口径: 收不回就如实报"仍被引用");
     * 满载环境下的验证用例可放宽轮数——那只是 GC 延迟, 不是泄漏。
     */
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool WaitForCollection(WeakReference weak, out string? report, int maxRounds = MaxGcRounds)
    {
        for (var round = 0; round < maxRounds && weak.IsAlive; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (weak.IsAlive)
                Thread.Sleep(10);
        }
        if (weak.IsAlive)
        {
            report = $"load context is still referenced after {maxRounds} GC rounds";
            return false;
        }
        report = null;
        return true;
    }
}
