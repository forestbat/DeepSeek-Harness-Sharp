using OpenTK.Graphics.Egl;

namespace Dsh.Tui;

/** OpenTK 的 GL 函数绑定上下文: 经 EGL 的 eglGetProcAddress 解析, pbuffer 与 GBM 两条宿主任共用。 */
internal sealed class EglBindingsContext : OpenTK.IBindingsContext
{
    public IntPtr GetProcAddress(string procName) => Egl.GetProcAddress(procName);
}
