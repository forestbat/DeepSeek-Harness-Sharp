using System.Reflection;
using System.Runtime.Versioning;

namespace Dsh.Tests;

/**
 * 当前测试程序集的目标框架, 例如 net11.0。
 *
 * 集成测试要定位同一次构建产出的 dsharp / dsharp-web / 插件样例, 它们的输出目录都带 TFM;
 * 把 TFM 写死会在升级目标框架后指向不存在的目录, 而失败信息只会说"可执行文件没构建"。
 */
internal static class BuildTargetFramework
{
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var framework = typeof(BuildTargetFramework).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        // ".NETCoreApp,Version=v11.0" → "net11.0"
        const string marker = "Version=v";
        var at = framework?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (framework is null || at < 0)
            throw new InvalidOperationException("测试程序集缺少 TargetFrameworkAttribute");
        return $"net{framework[(at + marker.Length)..]}";
    }
}
