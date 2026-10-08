using System.Runtime.InteropServices;

namespace Dsh.Plugins.Native;

/**
 * 原生插件故障遏制入口:包装 native/plugin_guard/guard.c 提供的 SEH(Windows)/sigsetjmp(POSIX)原语,
 * 让一次插件调用引发 AV/ILL/FPE 时被接住而不是终结进程。库名 dsh_plugin_guard(Windows 为 .dll, Unix 为 lib*.so)。
 */
public static unsafe class DshNativeGuard
{
    private const string Library = "dsh_plugin_guard";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_invoke_tool(nint fn, nint context, int handle, byte* input, byte* output, int capacity, out int result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_call_void_int(nint fn, nint context, out int result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_call_void(nint fn, nint context);

    /** 受护地调用工具函数; 返回 false 表示插件内发生故障(未写 result), 调用方应标记 Faulted 并卸载。 */
    public static bool TryInvokeTool(nint fn, nint context, int handle, byte* input, byte* output, int capacity, out int result)
        => dsh_guard_invoke_tool(fn, context, handle, input, output, capacity, out result) == 0;

    /** 受护地调用 int(void*) 函数(如 Activate); 返回 false 表示故障。 */
    public static bool TryCallInt(nint fn, nint context, out int result)
        => dsh_guard_call_void_int(fn, context, out result) == 0;

    /** 受护地调用 void(void*) 函数(如 Deactivate); 返回 false 表示故障。 */
    public static bool TryCallVoid(nint fn, nint context)
        => dsh_guard_call_void(fn, context) == 0;
}
