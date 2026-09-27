using System.Runtime.CompilerServices;

namespace Dsh.Tests;

/**
 * Linux 下以 `dotnet exec` 运行测试时 DOTNET_ROOT 常未设置, 子进程走 apphost 启动(pty daemon / dsh tui)会报
 * "You must install .NET to run this application"; 统一在进程启动时补上, 子进程按环境继承。
 */
internal static class DotnetRootInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            return;
        var root = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(root))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
    }
}
