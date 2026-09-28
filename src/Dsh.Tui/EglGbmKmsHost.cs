using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenTK.Graphics;
using OpenTK.Graphics.Egl;

namespace Dsh.Tui;

/**
 * 裸 TTY GBM/KMS 宿主: 无显示服务器时接管 DRM CRTC 直接上屏, 键盘/鼠标走 evdev。
 * 管线: drmSetMaster → KDSETMODE(KD_GRAPHICS) → drmModeGetResources 选 connector/encoder/CRTC/preferred mode
 * → gbm_create_device → eglGetPlatformDisplay(EGL_PLATFORM_GBM_MESA) → gbm_surface_create(XRGB8888, SCANOUT|RENDERING)
 * → eglCreateWindowSurface → 每帧 eglSwapBuffers + gbm_surface_lock_front_buffer + drmModeAddFB2 + PageFlip。
 */
internal sealed class EglGbmKmsHost : IGlSurfaceHostRunner
{
    private const int EglPlatformGbmMesa = 0x31D7;
    private const string DrmDeviceRoot = "/dev/dri";
    private const string DrmCardName = "card";
    private const string DrmCardPrefix = $"{DrmDeviceRoot}/{DrmCardName}";

    private readonly SafeFileHandle? _drmHandle;
    private readonly int _drmFd;
    private IntPtr _gbmDevice;
    private IntPtr _gbmSurface;
    private EGLDisplay _display;
    private EGLConfig _config;
    private EGLContext _context;
    private EGLSurface _surface;
    private uint _crtcId;
    private uint[] _connectors = [];
    private IntPtr _modePtr;
    private uint _width;
    private uint _height;
    private IntPtr _prevBo;
    private uint _prevFb;
    private bool _crtcSet;
    private bool _flipDone;
    private readonly DrmNative.PageFlipHandler _pageFlipHandler;
    private IntPtr _eventContextPtr;
    private VtConsole? _vt;
    private EvdevInput? _input;
    private bool _closeRequested;
    private bool _vtReleased;
    private bool _disposed;

    public (int Width, int Height) Size => ((int)_width, (int)_height);

    /** 一张卡的可用 KMS 配置(探测结果, 提交前不写实例字段)。 */
    private readonly record struct KmsSelection(uint CrtcId, uint ConnectorId, uint Width, uint Height, DrmNative.DrmModeModeInfo Mode);

    private EglGbmKmsHost(string? cardOverride, string? preferredCard)
    {
        _drmHandle = SelectCard(cardOverride, preferredCard);
        _drmFd = (int)_drmHandle.DangerousGetHandle();
        if (DrmNative.drmSetMaster(_drmFd) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            CleanupNative();
            throw new InvalidOperationException($"drmSetMaster 失败, errno={errno}");
        }

        _pageFlipHandler = OnPageFlip;
        try
        {
            _vt = VtConsole.EnterGraphicsMode();
            InitializeGbmEgl();
            _input = new EvdevInput(_width, _height);
        }
        catch
        {
            CleanupNative();
            throw;
        }
    }

    public static bool TryCreate(
        string? cardOverride,
        string? preferredCard,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EglGbmKmsHost? host,
        out string reason)
    {
        host = null;
        if (!OperatingSystem.IsLinux())
        {
            reason = "GBM/KMS 仅支持 Linux";
            return false;
        }
        try
        {
            host = new EglGbmKmsHost(cardOverride, preferredCard);
            reason = "";
            return true;
        }
        catch (Exception error)
        {
            reason = $"GBM/KMS 初始化失败: {error.Message}";
            return false;
        }
    }

    /** `--gpu-card` 的值: 纯卡号(1 → /dev/dri/card1)或卡节点路径原样使用。 */
    internal static string NormalizeCardPath(string value)
        => value.StartsWith('/') ? value : $"{DrmCardPrefix}{value}";

    public void MakeCurrent()
    {
        if (!Egl.MakeCurrent(_display, _surface, _surface, _context))
            throw new InvalidOperationException($"eglMakeCurrent 失败, err={Egl.GetError()}");
        GLLoader.LoadBindings(new EglBindingsContext());
    }

    public void Present()
    {
        if (_vtReleased)
            return;
        if (!Egl.SwapBuffers(_display, _surface))
            throw new InvalidOperationException($"eglSwapBuffers 失败, err={Egl.GetError()}");
        var bo = GbmNative.gbm_surface_lock_front_buffer(_gbmSurface);
        if (bo == IntPtr.Zero)
            return;
        var fb = AddFramebuffer(bo);
        if (!_crtcSet)
        {
            if (DrmNative.drmModeSetCrtc(_drmFd, _crtcId, fb, 0, 0, _connectors, _connectors.Length, _modePtr) != 0)
            {
                DrmNative.drmModeRmFB(_drmFd, fb);
                return;
            }
            _crtcSet = true;
        }
        else
        {
            _flipDone = false;
            if (DrmNative.drmModePageFlip(_drmFd, _crtcId, fb, DrmNative.DrmModePageFlipEvent, IntPtr.Zero) != 0)
            {
                DrmNative.drmModeRmFB(_drmFd, fb);
                return;
            }
            WaitForFlip();
        }

        if (_prevBo != IntPtr.Zero)
        {
            GbmNative.gbm_surface_release_buffer(_gbmSurface, _prevBo);
            DrmNative.drmModeRmFB(_drmFd, _prevFb);
        }
        _prevBo = bo;
        _prevFb = fb;
    }

    public void Run(IGpuHostClient client)
    {
        MakeCurrent();
        client.OnLoaded();
        client.OnResize((int)_width, (int)_height);
        while (!_closeRequested)
        {
            HandleVtSignals();
            if (_input is { DeviceCount: > 0 } input)
            {
                while (input.Read(10, out var inputEvent))
                {
                    // 切走期间按键照样到达 evdev: 丢弃, 不替别的 VT 消费输入
                    if (!_vtReleased)
                        Dispatch(client, inputEvent);
                }
            }
            else
            {
                Thread.Sleep(10);
            }
            // 切走期间不渲染不翻页: 已 drop master, KMS 调用只会被拒绝
            if (!_vtReleased && client.OnFrame())
                break;
        }
    }

    public void RequestClose() => _closeRequested = true;

    public string? ReadClipboard() => null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _input?.Dispose();
        CleanupNative();
    }

    private void Dispatch(IGpuHostClient client, EvdevEvent inputEvent)
    {
        switch (inputEvent.Kind)
        {
            case EvdevEventKind.Key:
                // Ctrl+Alt+Fn 是 VT 切换热键: K_OFF 下内核不处理, 由我们发起 VT_ACTIVATE
                // (武装了 VT_PROCESS 时内核回发释放信号, 走 HandleVtSignals 的握手)
                var key = inputEvent.Key;
                if (_vt is not null
                    && key.Key is >= ConsoleKey.F1 and <= ConsoleKey.F12
                    && (key.Modifiers & ConsoleModifiers.Control) != 0
                    && (key.Modifiers & ConsoleModifiers.Alt) != 0)
                {
                    _vt.ActivateVt(key.Key - ConsoleKey.F1 + 1);
                    return;
                }
                client.OnKey(key);
                break;
            case EvdevEventKind.MouseMove:
                client.OnMouseMove(inputEvent.X, inputEvent.Y);
                break;
            case EvdevEventKind.MouseButton:
                client.OnMouseButton(inputEvent.Pressed, inputEvent.X, inputEvent.Y);
                break;
            case EvdevEventKind.MouseWheel:
                client.OnMouseWheel(inputEvent.Wheel);
                break;
        }
    }

    /** VT_PROCESS 握手: 释放请求→交出 master 放行; 切回→重新接管并用最近一帧恢复画面。 */
    private void HandleVtSignals()
    {
        if (_vt is null)
            return;
        switch (_vt.TakePendingSignal())
        {
            case 1:
                DrmNative.drmDropMaster(_drmFd);
                _vt.AckRelease();
                _vtReleased = true;
                break;
            case 2:
                DrmNative.drmSetMaster(_drmFd);
                if (_prevFb != 0)
                    DrmNative.drmModeSetCrtc(_drmFd, _crtcId, _prevFb, 0, 0, _connectors, _connectors.Length, _modePtr);
                _vt.AckAcquire();
                _vtReleased = false;
                break;
        }
    }

    /**
     * 选卡: `--gpu-card` 严格指定(打不开/无显示输出就直接失败, 不偷偷换卡);
     * 否则把设置里选的卡当首选, 不可用时回退扫描(按 connected+有 mode 挑), 避免多卡机上默认开错卡。
     */
    private SafeFileHandle SelectCard(string? cardOverride, string? preferredCard)
    {
        if (cardOverride is { Length: > 0 } strict)
        {
            var path = NormalizeCardPath(strict);
            var handle = TryOpenCard(path);
            if (handle is null)
                throw new InvalidOperationException($"无法打开显式指定的显卡 {path} (需要 video 组或 root 权限)");
            if (!TryProbeKms((int)handle.DangerousGetHandle(), out var selection, out var probeReason))
            {
                handle.Dispose();
                throw new InvalidOperationException($"显式指定的显卡 {path} 不可用: {probeReason}");
            }
            ApplySelection(selection, path);
            return handle;
        }

        var candidates = new List<string>();
        if (preferredCard is { Length: > 0 } preferred)
            candidates.Add(preferred);
        candidates.AddRange(EnumerateCardNodes());
        var tried = new List<string>();
        foreach (var path in candidates.Distinct(StringComparer.Ordinal))
        {
            var handle = TryOpenCard(path);
            if (handle is null)
            {
                tried.Add($"{path}({(File.Exists(path) ? "打不开, 需要 video 组或 root" : "不存在")})");
                continue;
            }
            if (!TryProbeKms((int)handle.DangerousGetHandle(), out var selection, out var probeReason))
            {
                handle.Dispose();
                tried.Add($"{path}({probeReason})");
                continue;
            }
            ApplySelection(selection, path);
            return handle;
        }
        throw new InvalidOperationException($"没有可上屏的 DRM 卡: {string.Join("; ", tried)}");
    }

    /** `/dev/dri` 下实际存在的卡节点(cardN), 按卡号升序。 */
    private static IEnumerable<string> EnumerateCardNodes()
    {
        if (!Directory.Exists(DrmDeviceRoot))
            return [];
        try
        {
            return
            [
                .. Directory.EnumerateFiles(DrmDeviceRoot)
                    .Select(Path.GetFileName)
                    .Where(GpuCatalog.IsCardNodeName)
                    .OrderBy(name => long.TryParse(name.AsSpan(DrmCardName.Length), out var index) ? index : long.MaxValue)
                    .Select(name => $"{DrmDeviceRoot}/{name}"),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static SafeFileHandle? TryOpenCard(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /** 探测一张卡上是否有可用输出(connected connector + preferred mode + 可用 CRTC); 不改实例状态。 */
    private static bool TryProbeKms(int fd, out KmsSelection selection, out string reason)
    {
        selection = default;
        var resourcesPtr = DrmNative.drmModeGetResources(fd);
        if (resourcesPtr == IntPtr.Zero)
        {
            reason = $"drmModeGetResources 失败, errno={Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            var resources = Marshal.PtrToStructure<DrmNative.DrmModeRes>(resourcesPtr)!;
            for (var index = 0; index < resources.CountConnectors; index++)
            {
                var connectorId = DrmNative.ReadUInt32(resources.Connectors, index);
                var connectorPtr = DrmNative.drmModeGetConnector(fd, connectorId);
                if (connectorPtr == IntPtr.Zero)
                    continue;
                try
                {
                    var connector = Marshal.PtrToStructure<DrmNative.DrmModeConnector>(connectorPtr)!;
                    if (connector.Connection != DrmNative.DrmModeConnected || connector.CountModes == 0)
                        continue;
                    var mode = ChooseMode(connector);
                    selection = new KmsSelection(PickCrtc(fd, resources, connector), connector.ConnectorId, mode.Hdisplay, mode.Vdisplay, mode);
                    reason = "";
                    return true;
                }
                finally
                {
                    DrmNative.drmModeFreeConnector(connectorPtr);
                }
            }
        }
        finally
        {
            DrmNative.drmModeFreeResources(resourcesPtr);
        }
        reason = "没有已连接且带 mode 的显示输出";
        return false;
    }

    private void ApplySelection(KmsSelection selection, string path)
    {
        _crtcId = selection.CrtcId;
        _width = selection.Width;
        _height = selection.Height;
        _connectors = [selection.ConnectorId];
        _modePtr = Marshal.AllocHGlobal(Marshal.SizeOf<DrmNative.DrmModeModeInfo>());
        Marshal.StructureToPtr(selection.Mode, _modePtr, false);
        Console.Error.WriteLine($"gbm/kms: 上屏卡 {path} ({selection.Width}x{selection.Height})");
    }

    private static DrmNative.DrmModeModeInfo ChooseMode(DrmNative.DrmModeConnector connector)
    {
        for (var index = 0; index < connector.CountModes; index++)
        {
            var mode = DrmNative.ReadMode(connector.Modes, index);
            if ((mode.Type & DrmNative.DrmModeTypePreferred) != 0)
                return mode;
        }
        return DrmNative.ReadMode(connector.Modes, 0);
    }

    private static uint PickCrtc(int fd, DrmNative.DrmModeRes resources, DrmNative.DrmModeConnector connector)
    {
        if (connector.EncoderId == 0)
            throw new InvalidOperationException("connector 没有绑定 encoder");
        var encoderPtr = DrmNative.drmModeGetEncoder(fd, connector.EncoderId);
        if (encoderPtr == IntPtr.Zero)
            throw new InvalidOperationException($"drmModeGetEncoder({connector.EncoderId}) 失败");
        try
        {
            var encoder = Marshal.PtrToStructure<DrmNative.DrmModeEncoder>(encoderPtr)!;
            if (encoder.CrtcId != 0)
                return encoder.CrtcId;
            for (var index = 0; index < resources.CountCrtcs; index++)
            {
                if ((encoder.PossibleCrtcs & (1u << index)) != 0)
                    return DrmNative.ReadUInt32(resources.Crtcs, index);
            }
        }
        finally
        {
            DrmNative.drmModeFreeEncoder(encoderPtr);
        }
        throw new InvalidOperationException("encoder 没有可用 CRTC");
    }

    private void InitializeGbmEgl()
    {
        _gbmDevice = GbmNative.gbm_create_device(_drmFd);
        if (_gbmDevice == IntPtr.Zero)
            throw new InvalidOperationException("gbm_create_device 失败");

        _display = Egl.GetPlatformDisplay((Platform)EglPlatformGbmMesa, _gbmDevice, Array.Empty<IntPtr>());
        if (_display.Value == IntPtr.Zero)
            throw new InvalidOperationException($"eglGetPlatformDisplay(GBM) 失败, err={Egl.GetError()}");
        if (!Egl.Initialize(_display, out _, out _))
            throw new InvalidOperationException($"eglInitialize 失败, err={Egl.GetError()}");
        if (!Egl.BindAPI(RenderApi.OpenglApi))
            throw new InvalidOperationException($"eglBindAPI(OpenGL) 失败, err={Egl.GetError()}");

        int[] configAttribs = [0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3040, 4, 0x3033, 0x0004, 0x3038];
        var configs = new EGLConfig[8];
        var numConfigs = new int[1];
        if (!Egl.ChooseConfig(_display, configAttribs, configs, configs.Length, numConfigs) || numConfigs[0] == 0)
            throw new InvalidOperationException($"eglChooseConfig 失败, err={Egl.GetError()}");
        _config = configs[0];

        int[] contextAttribs = [0x3098, 3, 0x30FB, 3, 0x30FD, 0x1, 0x3038];
        _context = Egl.CreateContext(_display, _config, default, contextAttribs);
        if (_context.Value == IntPtr.Zero)
            throw new InvalidOperationException($"eglCreateContext 失败, err={Egl.GetError()}");

        _gbmSurface = GbmNative.gbm_surface_create(_gbmDevice, _width, _height, DrmNative.DrmFormatXrgb8888, GbmNative.GbmBoUseScanout | GbmNative.GbmBoUseRendering);
        if (_gbmSurface == IntPtr.Zero)
            throw new InvalidOperationException("gbm_surface_create 失败");

        _surface = Egl.CreateWindowSurface(_display, _config, _gbmSurface, Array.Empty<int>());
        if (_surface.Value == IntPtr.Zero)
            throw new InvalidOperationException($"eglCreateWindowSurface 失败, err={Egl.GetError()}");
        if (!Egl.MakeCurrent(_display, _surface, _surface, _context))
            throw new InvalidOperationException($"eglMakeCurrent 失败, err={Egl.GetError()}");
        GLLoader.LoadBindings(new EglBindingsContext());

        var eventContext = new DrmNative.DrmEventContext
        {
            Version = DrmNative.DrmEventContextVersion,
            PageFlipHandlerPointer = Marshal.GetFunctionPointerForDelegate(_pageFlipHandler),
        };
        _eventContextPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DrmNative.DrmEventContext>());
        Marshal.StructureToPtr(eventContext, _eventContextPtr, false);
    }

    private uint AddFramebuffer(IntPtr bo)
    {
        var handle = GbmNative.gbm_bo_get_handle(bo);
        var stride = GbmNative.gbm_bo_get_stride(bo);
        var width = GbmNative.gbm_bo_get_width(bo);
        var height = GbmNative.gbm_bo_get_height(bo);
        uint[] handles = [handle.U32, 0, 0, 0];
        uint[] pitches = [stride, 0, 0, 0];
        uint[] offsets = [0, 0, 0, 0];
        if (DrmNative.drmModeAddFB2(_drmFd, width, height, DrmNative.DrmFormatXrgb8888, handles, pitches, offsets, out var fb, 0) != 0)
            throw new InvalidOperationException($"drmModeAddFB2 失败, errno={Marshal.GetLastWin32Error()}");
        return fb;
    }

    private void WaitForFlip()
    {
        while (!_flipDone)
        {
            var fds = new[] { new PosixNative.PollFd { Fd = _drmFd, Events = PosixNative.PollIn } };
            var ready = PosixNative.poll(fds, 1, 1000);
            if (ready < 0)
                throw new InvalidOperationException($"poll(drm) 失败, errno={Marshal.GetLastWin32Error()}");
            if (ready == 0)
                continue;
            DrmNative.drmHandleEvent(_drmFd, _eventContextPtr);
        }
    }

    private void OnPageFlip(int fd, uint sequence, uint tvSec, uint tvUsec, IntPtr userData) => _flipDone = true;

    private void CleanupNative()
    {
        if (_gbmSurface != IntPtr.Zero && _prevBo != IntPtr.Zero)
            GbmNative.gbm_surface_release_buffer(_gbmSurface, _prevBo);
        if (_display.Value != IntPtr.Zero)
            Egl.MakeCurrent(_display, default, default, default);
        if (_surface.Value != IntPtr.Zero && _display.Value != IntPtr.Zero)
            Egl.DestroySurface(_display, _surface);
        if (_context.Value != IntPtr.Zero && _display.Value != IntPtr.Zero)
            Egl.DestroyContext(_display, _context);
        if (_gbmSurface != IntPtr.Zero)
            GbmNative.gbm_surface_destroy(_gbmSurface);
        if (_gbmDevice != IntPtr.Zero)
            GbmNative.gbm_device_destroy(_gbmDevice);
        if (_modePtr != IntPtr.Zero)
            Marshal.FreeHGlobal(_modePtr);
        if (_eventContextPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(_eventContextPtr);
        _vt?.Dispose();
        if (_drmFd != 0)
        {
            DrmNative.drmDropMaster(_drmFd);
            if (_prevFb != 0)
                DrmNative.drmModeRmFB(_drmFd, _prevFb);
        }
        _drmHandle?.Dispose();
    }
}
