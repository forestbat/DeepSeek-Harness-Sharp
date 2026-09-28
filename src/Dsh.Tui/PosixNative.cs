using System.Runtime.InteropServices;

namespace Dsh.Tui;

/** 裸 TTY 路径要用的 libc 原语: poll / ioctl / read 与 VT 控制台 ioctl 的结构体。 */
internal static class PosixNative
{
    public const short PollIn = 0x0001;
    public const short PollErr = 0x0008;
    public const short PollHup = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /** struct vt_mode: VT_SETMODE 的参数。 */
    [StructLayout(LayoutKind.Sequential)]
    public struct VtMode
    {
        public byte Mode;
        public byte WaitV;
        public short RelSig;
        public short AcqSig;
        public short FrSig;
    }

    /** struct vt_stat: VT_GETSTATE 的结果(v_active 为当前活动 VT 号)。 */
    [StructLayout(LayoutKind.Sequential)]
    public struct VtStat
    {
        public ushort Active;
        public ushort Signal;
        public ushort State;
    }

    /**
     * glibc x86_64 的 struct sigaction(152 字节): handler + 128 字节 mask + flags + restorer。
     * libc 包装器自己处理 restorer, 调用侧留 0 即可。
     */
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SigAction
    {
        public IntPtr Handler;
        public fixed byte Mask[128];
        public int Flags;
        public int Padding;
        public IntPtr Restorer;
    }

    [DllImport("libc", SetLastError = true)]
    public static extern int sigaction(int signum, ref SigAction act, IntPtr oldAct);

    [DllImport("libc", SetLastError = true)]
    public static extern int sigaction(int signum, IntPtr act, out SigAction oldAct);

    [DllImport("libc")]
    public static extern int __libc_current_sigrtmin();

    [DllImport("libc", SetLastError = true)]
    public static extern int poll([In, Out] PollFd[] fds, int nfds, int timeout);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int Ioctl(int fd, ulong request, int arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int Ioctl(int fd, ulong request, ref VtMode arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int Ioctl(int fd, ulong request, out VtStat arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int read(int fd, [Out] byte[] buffer, int count);
}
