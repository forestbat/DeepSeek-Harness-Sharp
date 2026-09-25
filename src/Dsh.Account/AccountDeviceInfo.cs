using System.Runtime.InteropServices;

namespace Dsh.Account;

/** 设备指纹只上报 OS/架构/版本, 不采集任何硬件或用户标识。 */
public static class AccountDeviceInfo
{
    public static string Model() => $"{OsName()}-{ArchName()}";

    public static string OsVersion() => $"{OsName()} {Environment.OSVersion.Version}";

    private static string OsName()
    {
        if (OperatingSystem.IsWindows())
            return "win32";
        if (OperatingSystem.IsMacOS())
            return "darwin";
        return "linux";
    }

    private static string ArchName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "ia32",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };
}
