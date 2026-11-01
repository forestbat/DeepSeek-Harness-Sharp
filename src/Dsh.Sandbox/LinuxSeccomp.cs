using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

internal static class LinuxSeccomp
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Instruction(ushort code, byte yes, byte no, uint value)
    {
        public readonly ushort Code = code;
        public readonly byte Yes = yes, No = no;
        public readonly uint Value = value;
    }

    private static readonly Lazy<byte[]> NoNetworkOrChildren = new(() => Build(false, false));
    private static readonly Lazy<byte[]> NoNetwork = new(() => Build(true, false));
    private static readonly Lazy<byte[]> NoChildren = new(() => Build(false, true));

    public static byte[] Create(bool allowChildProcesses, bool allowNetwork) => allowNetwork ? NoChildren.Value : allowChildProcesses ? NoNetwork.Value : NoNetworkOrChildren.Value;

    private static byte[] Build(bool allowChildProcesses, bool allowNetwork)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var audit = architecture switch
        {
            Architecture.X64 => 0xc000003eu,
            Architecture.Arm64 => 0xc00000b7u,
            _ => throw new PlatformNotSupportedException("The Linux seccomp boundary supports x64 and arm64 only.")
        };
        const uint allow = 0x7fff0000;
        const uint errno = 0x00050000;
        var instructions = new List<Instruction>
        {
            new(0x20, 0, 0, 4), // seccomp_data.arch
            new(0x15, 1, 0, audit),
            new(0x06, 0, 0, 0x80000000), // kill on a compat-ABI mismatch
            new(0x20, 0, 0, 0) // seccomp_data.nr
        };
        if (architecture == Architecture.X64)
        {
            instructions.Add(new(0x45, 0, 1, 0x40000000)); // Do not allow x32 to bypass native syscall numbers.
            instructions.Add(new(0x06, 0, 0, errno | 38));
        }
        if (!allowNetwork)
        {
            // Block pathname Unix sockets too, and prevent io_uring from creating sockets without passing socket's seccomp filter.
            var sockets = architecture == Architecture.X64 ? new uint[] { 41, 53, 425 } : [198, 199, 425];
            foreach (var syscall in sockets)
            {
                instructions.Add(new(0x15, 0, 1, syscall));
                instructions.Add(new(0x06, 0, 0, errno | 1));
            }
        }
        if (!allowChildProcesses)
        {
            if (architecture == Architecture.X64)
                foreach (var syscall in new uint[] { 57, 58 })
                {
                    instructions.Add(new(0x15, 0, 1, syscall));
                    instructions.Add(new(0x06, 0, 0, errno | 1));
                }
            instructions.Add(new(0x15, 0, 1, 435)); // clone3 has an indirect argument layout; libc can retry with clone.
            instructions.Add(new(0x06, 0, 0, errno | 38));
            instructions.Add(new(0x15, 0, 3, architecture == Architecture.X64 ? 56u : 220u));
            instructions.Add(new(0x20, 0, 0, 16)); // clone flags, seccomp_data.args[0]
            instructions.Add(new(0x45, 1, 0, 0x10000)); // CLONE_THREAD permits threads, not additional processes.
            instructions.Add(new(0x06, 0, 0, errno | 1));
        }
        instructions.Add(new(0x06, 0, 0, allow));
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(instructions)).ToArray();
    }
}
