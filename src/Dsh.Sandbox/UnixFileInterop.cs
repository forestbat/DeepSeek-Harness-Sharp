using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

internal static class UnixFileInterop
{
    private static readonly bool VariadicArgumentsOnStack = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static int Fcntl(int descriptor, int command, nint argument)
        => VariadicArgumentsOnStack
            ? fcntlAfterRegisters(descriptor, command, 0, 0, 0, 0, 0, 0, argument)
            : fcntl(descriptor, command, argument);



    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, nint argument);

    // Darwin arm64 reads the variadic value from the first stack slot after eight register arguments.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntlAfterRegisters(int descriptor, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);

}
