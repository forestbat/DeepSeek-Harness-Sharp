using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tui;

/** VT 控制台模式切换: 进 KD_GRAPHICS 停掉内核控制台绘制, 退出恢复 KD_TEXT。 */
internal sealed class VtConsole : IDisposable
{
    private const ulong KdSetMode = 0x4B3A;
    private const int KdText = 0;
    private const int KdGraphics = 1;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private VtConsole(SafeFileHandle handle) => _handle = handle;

    private int Fd => (int)_handle.DangerousGetHandle();

    public static VtConsole EnterGraphicsMode()
    {
        var handle = File.OpenHandle("/dev/tty0", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var console = new VtConsole(handle);
        if (PosixNative.Ioctl(console.Fd, KdSetMode, KdGraphics) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            console.Dispose();
            throw new InvalidOperationException($"KDSETMODE(KD_GRAPHICS) 失败, errno={errno}");
        }
        return console;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        PosixNative.Ioctl(Fd, KdSetMode, KdText);
        _handle.Dispose();
    }
}
