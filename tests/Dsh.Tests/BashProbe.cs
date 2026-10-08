using System.Diagnostics;

namespace Dsh.Tests;

/** bash 可用性探测：WSL/Git Bash 无响应时让依赖外部 shell 的用例跳过而不是挂死。结果进程级缓存。 */
internal static class BashProbe
{
    private const int ProbeTimeoutMs = 5000;

    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo("bash", "-c \"exit 0\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(ProbeTimeoutMs) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // 探测超时(如本机 WSL 挂死)时子进程仍在运行, 必须连子进程树杀掉, 否则会一直占着句柄、拖住整个测试进程。
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出或无权终止: 探测结果已定, 这里不再抛出。
            }
            process?.Dispose();
        }
    }
}
