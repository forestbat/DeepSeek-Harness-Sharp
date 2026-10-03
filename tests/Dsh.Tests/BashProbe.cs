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
        try
        {
            using var process = Process.Start(new ProcessStartInfo("bash", "-c \"exit 0\"")
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
    }
}
