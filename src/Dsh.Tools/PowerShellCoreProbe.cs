using System.Diagnostics;
using Dsh.Core;

namespace Dsh.Tools;

/**
 * `pwsh` 是 PowerShell Core(6+)的可执行名; `powershell` 才是 Windows PowerShell 5.1。
 * 只有确认真能启动且主版本 >= 6 才把 pwsh 工具给模型看; 只装了 5.1 的机器上由 powershell 工具负责。
 */
public static class PowerShellCoreProbe
{
    private const string Executable = "pwsh";
    private const int MinimumMajorVersion = 6;
    private const int ProbeTimeoutMs = 10_000;
    private static readonly Lazy<bool> Cached = new(Detect, isThreadSafe: true);

    public static bool IsAvailable() => Cached.Value;

    private static bool Detect()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Executable,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0 && int.TryParse(output.Trim(), out var major) && major >= MinimumMajorVersion;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
