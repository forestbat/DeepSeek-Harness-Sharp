using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using Egl = OpenTK.Graphics.Egl.Egl;
using GLFW = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;

namespace Dsh.Tests;

/** GL_TIME_ELAPSED / ARB_pipeline_statistics 计数器: 把"纯 GPU 执行"与"提交+同步"分开计量。 */
internal sealed class GpuStatQueries : IDisposable
{
    private const uint QueryResult = 0x8866;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetQueryObjectui64VFn(uint id, uint pname, out ulong result);

    private static GetQueryObjectui64VFn? _readQueryVolatile;

    private readonly int _timeQuery = GL.GenQuery();
    private readonly int _verticesQuery = GL.GenQuery();
    private readonly int _primitivesQuery = GL.GenQuery();
    private readonly int _vsInvocationsQuery = GL.GenQuery();
    private readonly int _primitivesGeneratedQuery = GL.GenQuery();
    private bool _timeOk;
    private bool _statsOk;

    public ulong ElapsedNs { get; private set; }

    public ulong Vertices { get; private set; }

    public ulong Primitives { get; private set; }

    public ulong VsInvocations { get; private set; }

    public ulong PrimitivesGenerated { get; private set; }

    public bool SupportsStats => _statsOk;

    /** GL_TIME_ELAPSED 是 3.3 core 能力,ARB_pipeline_statistics 是 4.6 扩展, 分开探测(Mesa/D3D12 只支持前者)。探测前排空遗留错误标志, 否则第一个 GetError 读到的是上下文创建期的陈渣。 */
    public static GpuStatQueries? TryCreate()
    {
        var queries = new GpuStatQueries();
        while (GL.GetError() != ErrorCode.NoError) { }
        GL.BeginQuery(QueryTarget.TimeElapsed, queries._timeQuery);
        GL.EndQuery(QueryTarget.TimeElapsed);
        queries._timeOk = GL.GetError() == ErrorCode.NoError;
        GL.BeginQuery(QueryTarget.VerticesSubmitted, queries._verticesQuery);
        GL.EndQuery(QueryTarget.VerticesSubmitted);
        queries._statsOk = GL.GetError() == ErrorCode.NoError;
        if (!queries._timeOk && !queries._statsOk)
        {
            queries.Dispose();
            return null;
        }
        return queries;
    }

    public void Begin()
    {
        if (_timeOk)
            GL.BeginQuery(QueryTarget.TimeElapsed, _timeQuery);
        if (!_statsOk)
            return;
        GL.BeginQuery(QueryTarget.VerticesSubmitted, _verticesQuery);
        GL.BeginQuery(QueryTarget.PrimitivesSubmitted, _primitivesQuery);
        GL.BeginQuery(QueryTarget.VertexShaderInvocations, _vsInvocationsQuery);
        GL.BeginQuery(QueryTarget.PrimitivesGenerated, _primitivesGeneratedQuery);
    }

    public void End()
    {
        if (_timeOk)
            GL.EndQuery(QueryTarget.TimeElapsed);
        if (!_statsOk)
            return;
        GL.EndQuery(QueryTarget.VerticesSubmitted);
        GL.EndQuery(QueryTarget.PrimitivesSubmitted);
        GL.EndQuery(QueryTarget.VertexShaderInvocations);
        GL.EndQuery(QueryTarget.PrimitivesGenerated);
    }

    public void Collect()
    {
        if (_timeOk)
            ElapsedNs = ReadElapsedNsManual();
        if (!_statsOk)
            return;
        Vertices = GL.GetQueryObjectui64(_verticesQuery, QueryObjectParameterName.QueryResult);
        Primitives = GL.GetQueryObjectui64(_primitivesQuery, QueryObjectParameterName.QueryResult);
        VsInvocations = GL.GetQueryObjectui64(_vsInvocationsQuery, QueryObjectParameterName.QueryResult);
        PrimitivesGenerated = GL.GetQueryObjectui64(_primitivesGeneratedQuery, QueryObjectParameterName.QueryResult);
    }

    public void Dispose()
    {
        GL.DeleteQuery(_timeQuery);
        GL.DeleteQuery(_verticesQuery);
        GL.DeleteQuery(_primitivesQuery);
        GL.DeleteQuery(_vsInvocationsQuery);
        GL.DeleteQuery(_primitivesGeneratedQuery);
    }

    /** OpenTK 绑定读 TIME_ELAPSED 恒 0(同一上下文中计数器 query 正常; NVIDIA WGL 与 Mesa 都复现), 直取函数指针读。 */
    private ulong ReadElapsedNsManual()
    {
        var fn = _readQueryVolatile ??= ResolveQueryReader();
        if (fn is null)
            return 0;
        fn((uint)_timeQuery, QueryResult, out var value);
        return value;
    }

    private static GetQueryObjectui64VFn? ResolveQueryReader()
    {
        var pointer = GLFW.GetProcAddress("glGetQueryObjectui64v");
        if (pointer == IntPtr.Zero)
            pointer = Egl.GetProcAddress("glGetQueryObjectui64v");
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<GetQueryObjectui64VFn>(pointer);
    }
}
