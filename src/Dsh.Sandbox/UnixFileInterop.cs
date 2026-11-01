using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

internal static class UnixFileInterop
{
    private static readonly bool VariadicArgumentsOnStack = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static int Fcntl(int descriptor, int command, nint argument)
        => VariadicArgumentsOnStack
            ? fcntlAfterRegisters(descriptor, command, 0, 0, 0, 0, 0, 0, argument)
            : fcntl(descriptor, command, argument);

    public static unsafe int Fcntl(int descriptor, int command, byte[] buffer)
    {
        fixed (byte* pointer = buffer)
            return Fcntl(descriptor, command, (nint)pointer);
    }

    public static int OpenAt(int directory, string name, int flags, uint mode)
        => VariadicArgumentsOnStack
            ? openatAfterRegisters(directory, name, flags, 0, 0, 0, 0, 0, (nint)mode)
            : openat(directory, name, flags, mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, nint argument);

    // Darwin arm64 reads the variadic value from the first stack slot after eight register arguments.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntlAfterRegisters(int descriptor, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openatAfterRegisters(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, nint x3, nint x4, nint x5, nint x6, nint x7, nint mode);
}
