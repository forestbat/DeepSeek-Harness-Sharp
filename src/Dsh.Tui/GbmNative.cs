using System.Runtime.InteropServices;

namespace Dsh.Tui;

/** libgbm 薄 P/Invoke: 创建 DRM 后备缓冲表面并锁定/释放前台 BO。 */
internal static class GbmNative
{
    public const uint GbmBoUseScanout = 1 << 0;
    public const uint GbmBoUseRendering = 1 << 2;

    private const string Lib = "libgbm.so.1";

    /** struct gbm_bo_handle 是 8 字节联合: 按值返回时用 ulong 承载最宽的成员。 */
    [StructLayout(LayoutKind.Explicit)]
    public struct GbmBoHandle
    {
        [FieldOffset(0)]
        public uint U32;
        [FieldOffset(0)]
        public ulong U64;
        [FieldOffset(0)]
        public IntPtr Ptr;
    }

    [DllImport(Lib)]
    public static extern IntPtr gbm_create_device(int fd);

    [DllImport(Lib)]
    public static extern void gbm_device_destroy(IntPtr device);

    [DllImport(Lib)]
    public static extern IntPtr gbm_surface_create(IntPtr device, uint width, uint height, uint format, uint flags);

    [DllImport(Lib)]
    public static extern void gbm_surface_destroy(IntPtr surface);

    [DllImport(Lib)]
    public static extern IntPtr gbm_surface_lock_front_buffer(IntPtr surface);

    [DllImport(Lib)]
    public static extern void gbm_surface_release_buffer(IntPtr surface, IntPtr bo);

    [DllImport(Lib)]
    public static extern GbmBoHandle gbm_bo_get_handle(IntPtr bo);

    [DllImport(Lib)]
    public static extern uint gbm_bo_get_stride(IntPtr bo);

    [DllImport(Lib)]
    public static extern uint gbm_bo_get_width(IntPtr bo);

    [DllImport(Lib)]
    public static extern uint gbm_bo_get_height(IntPtr bo);
}
