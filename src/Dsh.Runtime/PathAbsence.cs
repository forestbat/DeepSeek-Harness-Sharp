using System.ComponentModel;

namespace Dsh.Runtime;

public static class PathAbsence
{
    private const int WindowsFileNotFound = 2;
    private const int WindowsPathNotFound = 3;
    private const int WindowsNotDirectory = 267;
    private const int PosixNoSuchFileOrDirectory = 2;
    private const int PosixNotDirectory = 20;

    public static bool IsAbsent(Exception error)
        => error is FileNotFoundException or DirectoryNotFoundException
            || error is IOException { HResult: var hResult } && (hResult & ushort.MaxValue) == WindowsNotDirectory
            || error is Win32Exception native && (OperatingSystem.IsWindows()
                ? native.NativeErrorCode is WindowsFileNotFound or WindowsPathNotFound or WindowsNotDirectory
                : native.NativeErrorCode is PosixNoSuchFileOrDirectory or PosixNotDirectory);
}
