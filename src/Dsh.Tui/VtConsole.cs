using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tui;

/**
 * VT 控制台接管: KD_GRAPHICS 停掉内核控制台绘制, K_OFF 防按键漏进 VT 行律,
 * VT_PROCESS 接管 Ctrl+Alt+Fn 切换(内核改为发信号: 释放>drmDropMaster>VT_RELDISP(1)放行;
 * 切回>drmSetMaster+恢复 CRTC>VT_RELDISP(2)), 退出恢复 AUTO/XLATE/TEXT。
 * 信令用实时信号(SIGRTMIN+4/+5): .NET 的 PosixSignal 枚举不含 SIGUSR1/2, 且 CoreCLR 占用 SIGUSR1;
 * 信号处理器只做一次静态字段写入(信号上下文里不做任何运行时再入), 由宿主循环轮询消费。
 */
internal sealed class VtConsole : IDisposable
{
    private const ulong KdSetMode = 0x4B3A;
    private const ulong KdSetKbMode = 0x4B45;
    private const ulong VtSetMode = 0x5602;
    private const ulong VtGetState = 0x5603;
    private const ulong VtRelDisp = 0x5605;
    private const ulong VtActivate = 0x5606;
    private const int KdText = 0;
    private const int KdGraphics = 1;
    private const int KXlate = 1;
    private const int KOff = 4;
    private const byte VtAuto = 0;
    private const byte VtProcess = 1;
    private const int SaRestart = 0x10000000;

    private static readonly unsafe delegate* unmanaged[Cdecl]<int, void> ReleaseHandlerPtr = &OnVtRelease;
    private static readonly unsafe delegate* unmanaged[Cdecl]<int, void> AcquireHandlerPtr = &OnVtAcquire;
    private static int _pendingVtSignal;

    private readonly SafeFileHandle _handle;
    private PosixNative.SigAction _oldReleaseAction;
    private PosixNative.SigAction _oldAcquireAction;
    private bool _disposed;

    private VtConsole(SafeFileHandle handle) => _handle = handle;

    private int Fd => (int)_handle.DangerousGetHandle();

    public int VtNumber { get; private set; }

    /** VT_PROCESS 是否成功武装(信号被运行库/驱动占用时自动放弃, 回退 VT_AUTO 行为)。 */
    public bool ProcessSwitchArmed { get; private set; }

    public int ReleaseSignal { get; private set; }

    public int AcquireSignal { get; private set; }

    public static VtConsole EnterGraphicsMode()
    {
        var vtNumber = QueryActiveVt();
        // 直接开具体 VT 而非 /dev/tty0: tty0 随活动 VT 漂移, 切换流程中 VT_RELDISP 必须落在自己的 VT 上
        var handle = File.OpenHandle($"/dev/tty{vtNumber}", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var console = new VtConsole(handle) { VtNumber = vtNumber };
        if (PosixNative.Ioctl(console.Fd, KdSetMode, KdGraphics) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            console.Dispose();
            throw new InvalidOperationException($"KDSETMODE(KD_GRAPHICS) 失败, errno={errno}");
        }
        // K_OFF: 键盘事件只走 evdev, 不进 VT 行律(否则输入回显/缓存在控制台 tty, 退出后被 getty 读到)。
        // Ctrl+Alt+Fn 切换热键由 EvdevInput 侧自行识别后 VT_ACTIVATE(K_OFF 下内核不处理该组合键)。
        if (PosixNative.Ioctl(console.Fd, KdSetKbMode, KOff) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            console.Dispose();
            throw new InvalidOperationException($"KDSKBMODE(K_OFF) 失败, errno={errno}");
        }
        console.ArmProcessSwitch();
        return console;
    }

    /** 取走待处理的 VT 信令: 0 无 / 1 释放请求 / 2 重新获得。 */
    public int TakePendingSignal() => Interlocked.Exchange(ref _pendingVtSignal, 0);

    /** 放行切走(必须先 drmDropMaster)。 */
    public void AckRelease()
    {
        if (PosixNative.Ioctl(Fd, VtRelDisp, 1) != 0)
            Console.Error.WriteLine($"VT_RELDISP(放行) 失败, errno={Marshal.GetLastWin32Error()}");
    }

    /** 确认重新获得(必须先 drmSetMaster + 恢复 CRTC)。 */
    public void AckAcquire()
    {
        if (PosixNative.Ioctl(Fd, VtRelDisp, 2) != 0)
            Console.Error.WriteLine($"VT_RELDISP(获得) 失败, errno={Marshal.GetLastWin32Error()}");
    }

    /** 主动切走(响应 Ctrl+Alt+Fn 热键); 武装了 VT_PROCESS 时内核会先回发释放信号。 */
    public void ActivateVt(int vtNumber)
    {
        if (vtNumber == VtNumber)
            return;
        PosixNative.Ioctl(Fd, VtActivate, vtNumber);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ProcessSwitchArmed)
        {
            var auto = new PosixNative.VtMode { Mode = VtAuto };
            PosixNative.Ioctl(Fd, VtSetMode, ref auto);
            PosixNative.sigaction(ReleaseSignal, ref _oldReleaseAction, IntPtr.Zero);
            PosixNative.sigaction(AcquireSignal, ref _oldAcquireAction, IntPtr.Zero);
        }
        PosixNative.Ioctl(Fd, KdSetKbMode, KXlate);
        PosixNative.Ioctl(Fd, KdSetMode, KdText);
        _handle.Dispose();
    }

    private static int QueryActiveVt()
    {
        try
        {
            using var tty0 = File.OpenHandle("/dev/tty0", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (PosixNative.Ioctl((int)tty0.DangerousGetHandle(), VtGetState, out PosixNative.VtStat stat) == 0 && stat.Active > 0)
                return stat.Active;
        }
        catch (Exception)
        {
            // 退化到 tty1
        }
        return 1;
    }

    private void ArmProcessSwitch()
    {
        var rtMin = PosixNative.__libc_current_sigrtmin();
        ReleaseSignal = rtMin + 4;
        AcquireSignal = rtMin + 5;
        // 能力探测: 信号被占用(CoreCLR/驱动)时不武装, 保持 VT_AUTO 现状
        if (PosixNative.sigaction(ReleaseSignal, IntPtr.Zero, out _oldReleaseAction) != 0
            || PosixNative.sigaction(AcquireSignal, IntPtr.Zero, out _oldAcquireAction) != 0
            || _oldReleaseAction.Handler != IntPtr.Zero
            || _oldAcquireAction.Handler != IntPtr.Zero)
            return;

        unsafe
        {
            var releaseAction = new PosixNative.SigAction { Handler = (IntPtr)ReleaseHandlerPtr, Flags = SaRestart };
            var acquireAction = new PosixNative.SigAction { Handler = (IntPtr)AcquireHandlerPtr, Flags = SaRestart };
            if (PosixNative.sigaction(ReleaseSignal, ref releaseAction, IntPtr.Zero) != 0
                || PosixNative.sigaction(AcquireSignal, ref acquireAction, IntPtr.Zero) != 0)
                return;
        }

        var mode = new PosixNative.VtMode
        {
            Mode = VtProcess,
            WaitV = 0,
            RelSig = (short)ReleaseSignal,
            AcqSig = (short)AcquireSignal,
            FrSig = 0,
        };
        if (PosixNative.Ioctl(Fd, VtSetMode, ref mode) != 0)
        {
            PosixNative.sigaction(ReleaseSignal, ref _oldReleaseAction, IntPtr.Zero);
            PosixNative.sigaction(AcquireSignal, ref _oldAcquireAction, IntPtr.Zero);
            return;
        }
        ProcessSwitchArmed = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVtRelease(int signal) => Interlocked.Exchange(ref _pendingVtSignal, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVtAcquire(int signal) => Interlocked.Exchange(ref _pendingVtSignal, 2);
}
