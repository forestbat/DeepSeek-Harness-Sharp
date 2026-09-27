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
 * 未在裸金属 DRM/TTY 上实跑(开发机为 Windows/WSL, 无 /dev/dri), 需真机验证。
 */
internal sealed class EglGbmKmsHost : IGlSurfaceHostRunner
{
    private const int EglPlatformGbmMesa = 0x31D7;
    private const string DrmCardPrefix = "/dev/dri/card";

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
    private bool _disposed;

    public (int Width, int Height) Size => ((int)_width, (int)_height);

    private EglGbmKmsHost()
    {
        _drmHandle = OpenCard();
        _drmFd = (int)_drmHandle.DangerousGetHandle();
        if (DrmNative.drmSetMaster(_drmFd) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            _drmHandle.Dispose();
            throw new InvalidOperationException($"drmSetMaster 失败, errno={errno}");
        }

        _pageFlipHandler = OnPageFlip;
        try
        {
            _vt = VtConsole.EnterGraphicsMode();
            InitializeKms();
            InitializeGbmEgl();
            _input = new EvdevInput(_width, _height);
        }
        catch
        {
            CleanupNative();
            throw;
        }
    }

    public static bool TryCreate([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EglGbmKmsHost? host, out string reason)
    {
        host = null;
        if (!OperatingSystem.IsLinux())
        {
            reason = "GBM/KMS 仅支持 Linux";
            return false;
        }
        try
        {
            host = new EglGbmKmsHost();
            reason = "";
            return true;
        }
        catch (Exception error)
        {
            reason = $"GBM/KMS 初始化失败: {error.Message}";
            return false;
        }
    }

    public void MakeCurrent()
    {
        if (!Egl.MakeCurrent(_display, _surface, _surface, _context))
            throw new InvalidOperationException($"eglMakeCurrent 失败, err={Egl.GetError()}");
        GLLoader.LoadBindings(new EglBindingsContext());
    }

    public void Present()
    {
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
            if (_input is { DeviceCount: > 0 } input)
            {
                while (input.Read(10, out var inputEvent))
                    Dispatch(client, inputEvent);
            }
            else
            {
                Thread.Sleep(10);
            }
            if (client.OnFrame())
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

    private static void Dispatch(IGpuHostClient client, EvdevEvent inputEvent)
    {
        switch (inputEvent.Kind)
        {
            case EvdevEventKind.Key:
                client.OnKey(inputEvent.Key);
                break;
            case EvdevEventKind.Text:
                client.OnText(inputEvent.Text);
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

    private static SafeFileHandle OpenCard()
    {
        for (var index = 0; index < 10; index++)
        {
            var path = $"{DrmCardPrefix}{index}";
            if (!File.Exists(path))
                continue;
            try
            {
                return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        throw new InvalidOperationException($"找不到可打开的 {DrmCardPrefix}N (需要 video 组或 root 权限)");
    }

    private void InitializeKms()
    {
        var resourcesPtr = DrmNative.drmModeGetResources(_drmFd);
        if (resourcesPtr == IntPtr.Zero)
            throw new InvalidOperationException($"drmModeGetResources 失败, errno={Marshal.GetLastWin32Error()}");
        try
        {
            var resources = Marshal.PtrToStructure<DrmNative.DrmModeRes>(resourcesPtr)!;
            for (var index = 0; index < resources.CountConnectors; index++)
            {
                var connectorId = DrmNative.ReadUInt32(resources.Connectors, index);
                var connectorPtr = DrmNative.drmModeGetConnector(_drmFd, connectorId);
                if (connectorPtr == IntPtr.Zero)
                    continue;
                try
                {
                    var connector = Marshal.PtrToStructure<DrmNative.DrmModeConnector>(connectorPtr)!;
                    if (connector.Connection != DrmNative.DrmModeConnected || connector.CountModes == 0)
                        continue;
                    SelectConnector(resources, connector);
                    return;
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
        throw new InvalidOperationException("没有已连接且带 mode 的显示输出");
    }

    private void SelectConnector(DrmNative.DrmModeRes resources, DrmNative.DrmModeConnector connector)
    {
        var mode = ChooseMode(connector);
        _crtcId = PickCrtc(resources, connector);
        _width = mode.Hdisplay;
        _height = mode.Vdisplay;
        _modePtr = Marshal.AllocHGlobal(Marshal.SizeOf<DrmNative.DrmModeModeInfo>());
        Marshal.StructureToPtr(mode, _modePtr, false);
        _connectors = [connector.ConnectorId];
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

    private uint PickCrtc(DrmNative.DrmModeRes resources, DrmNative.DrmModeConnector connector)
    {
        if (connector.EncoderId == 0)
            throw new InvalidOperationException("connector 没有绑定 encoder");
        var encoderPtr = DrmNative.drmModeGetEncoder(_drmFd, connector.EncoderId);
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
