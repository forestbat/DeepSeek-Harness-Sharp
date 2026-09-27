using System.Runtime.InteropServices;
using OpenTK.Graphics;
using OpenTK.Graphics.Egl;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tui;

/**
 * 无头 EGL pbuffer 宿主: Linux 无显示服务器时用 EGL 平台设备枚举选卡, 建 pbuffer 供压测/CI 真无头渲染。
 * display 直接来自 EGL_PLATFORM_DEVICE_EXT 枚举出的设备; Windows/有显示服务器走 GlfwWindowHost。
 */
public sealed class EglPbufferHost : IGlSurfaceHost
{
    private const int PlatformDeviceExt = 0x313F;
    private const int ProbeSurfaceSize = 16;

    private readonly EGLDisplay _display;
    private readonly EGLContext _context;
    private readonly EGLSurface _surface;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    /** 选中的 GL 设备的 GL_RENDERER 字符串。 */
    public string Renderer { get; }

    /** 枚举到的全部设备描述(探针渲染器名或失败原因), 供报告多卡环境。 */
    public IReadOnlyList<string> EnumeratedDevices { get; }

    public (int Width, int Height) Size => (_width, _height);

    private EglPbufferHost(EGLDisplay display, EGLContext context, EGLSurface surface, int width, int height, string renderer, IReadOnlyList<string> enumeratedDevices)
    {
        _display = display;
        _context = context;
        _surface = surface;
        _width = width;
        _height = height;
        Renderer = renderer;
        EnumeratedDevices = enumeratedDevices;
    }

    public static EglPbufferHost Create(int width, int height)
    {
        var queryDevices = Marshal.GetDelegateForFunctionPointer<QueryDevicesExt>(Egl.GetProcAddress("eglQueryDevicesEXT"));
        var count = new int[1];
        if (queryDevices(0, null, count) == 0 || count[0] == 0)
            throw new InvalidOperationException($"eglQueryDevicesEXT 失败, err={Egl.GetError()}");
        var devices = new IntPtr[count[0]];
        if (queryDevices(count[0], devices, count) == 0)
            throw new InvalidOperationException($"eglQueryDevicesEXT 枚举失败, err={Egl.GetError()}");

        var candidates = new List<EglCandidate>();
        var failures = new List<string>();
        var enumerated = new List<string>();
        foreach (var device in devices)
        {
            var candidate = EglCandidate.TryProbe(device, out var failure);
            if (candidate is not null)
            {
                candidates.Add(candidate);
                enumerated.Add(candidate.Renderer);
            }
            else
            {
                failures.Add(failure);
                enumerated.Add($"probe 失败: {failure}");
            }
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException($"没有任何 EGL 设备能创建 GL 3.3 core 上下文 (devices={devices.Length}): {string.Join(" | ", failures)}");

        var chosen = candidates.OrderByDescending(candidate => candidate.Score).First();
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, chosen))
                candidate.Dispose();
        }

        int[] surfaceAttribs = [0x3057, width, 0x3056, height, 0x3038];
        var surface = Egl.CreatePbufferSurface(chosen.Display, chosen.Config, surfaceAttribs);
        if (surface.Value == IntPtr.Zero)
            throw new InvalidOperationException($"eglCreatePbufferSurface 失败, err={Egl.GetError()}");
        if (!Egl.MakeCurrent(chosen.Display, surface, surface, chosen.Context))
            throw new InvalidOperationException($"eglMakeCurrent 失败, err={Egl.GetError()}");
        chosen.DropProbeSurface();
        GLLoader.LoadBindings(new EglBindingsContext());
        return new EglPbufferHost(chosen.Display, chosen.Context, surface, width, height, chosen.Renderer, enumerated);
    }

    public void MakeCurrent()
    {
        if (!Egl.MakeCurrent(_display, _surface, _surface, _context))
            throw new InvalidOperationException($"eglMakeCurrent 失败, err={Egl.GetError()}");
        GLLoader.LoadBindings(new EglBindingsContext());
    }

    public void Present()
    {
        // pbuffer 没有可呈现表面: 提交命令保证后续 ReadPixels 能读回最新帧。
        GL.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Egl.MakeCurrent(_display, default, default, default);
        Egl.DestroySurface(_display, _surface);
        Egl.DestroyContext(_display, _context);
    }

    private sealed class EglCandidate : IDisposable
    {
        private EGLSurface _probeSurface;

        private EglCandidate(EGLDisplay display, EGLConfig config, EGLContext context, EGLSurface probeSurface, string renderer)
        {
            Display = display;
            Config = config;
            Context = context;
            _probeSurface = probeSurface;
            Renderer = renderer;
            Score = ScoreRenderer(renderer);
        }

        public EGLDisplay Display { get; }
        public EGLConfig Config { get; }
        public EGLContext Context { get; }
        public string Renderer { get; }
        public int Score { get; }

        public static EglCandidate? TryProbe(IntPtr device, out string failure)
        {
            failure = "";
            try
            {
                var display = Egl.GetPlatformDisplay((Platform)PlatformDeviceExt, device, Array.Empty<IntPtr>());
                if (display.Value == IntPtr.Zero)
                {
                    failure = $"GetPlatformDisplay err={Egl.GetError()}";
                    return null;
                }
                if (!Egl.Initialize(display, out _, out _))
                {
                    failure = $"Initialize err={Egl.GetError()}";
                    return null;
                }
                if (!Egl.BindAPI(RenderApi.OpenglApi))
                {
                    failure = $"BindAPI err={Egl.GetError()}";
                    return null;
                }
                int[] configAttribs = [0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3040, 4, 0x3033, 0x1, 0x3038];
                var configs = new EGLConfig[8];
                var numConfigs = new int[1];
                if (!Egl.ChooseConfig(display, configAttribs, configs, configs.Length, numConfigs) || numConfigs[0] == 0)
                {
                    failure = $"ChooseConfig err={Egl.GetError()}";
                    return null;
                }
                int[] contextAttribs = [0x3098, 3, 0x30FB, 3, 0x30FD, 0x1, 0x3038];
                var context = Egl.CreateContext(display, configs[0], default, contextAttribs);
                if (context.Value == IntPtr.Zero)
                {
                    failure = $"CreateContext err={Egl.GetError()}";
                    return null;
                }
                int[] surfaceAttribs = [0x3057, ProbeSurfaceSize, 0x3056, ProbeSurfaceSize, 0x3038];
                var probeSurface = Egl.CreatePbufferSurface(display, configs[0], surfaceAttribs);
                if (probeSurface.Value == IntPtr.Zero || !Egl.MakeCurrent(display, probeSurface, probeSurface, context))
                {
                    failure = $"Pbuffer/MakeCurrent err={Egl.GetError()}";
                    Egl.DestroyContext(display, context);
                    return null;
                }
                GLLoader.LoadBindings(new EglBindingsContext());
                var renderer = GL.GetString(OpenTK.Graphics.OpenGL.StringName.Renderer) ?? "";
                return new EglCandidate(display, configs[0], context, probeSurface, renderer);
            }
            catch (Exception error)
            {
                failure = error.Message;
                return null;
            }
        }

        public void Dispose()
        {
            Egl.MakeCurrent(Display, default, default, default);
            Egl.DestroySurface(Display, _probeSurface);
            Egl.DestroyContext(Display, Context);
        }

        public void DropProbeSurface()
        {
            Egl.DestroySurface(Display, _probeSurface);
            _probeSurface = default;
        }
    }

    private static int ScoreRenderer(string renderer)
    {
        if (renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("basic render", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("lavapipe", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (renderer.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("geforce", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("quadro", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 2;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryDevicesExt(int maxDevices, [Out] IntPtr[]? devices, [Out] int[] numDevices);
}
