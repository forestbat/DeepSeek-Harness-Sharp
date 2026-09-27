using System.Runtime.InteropServices;

namespace Dsh.Tui;

/** libdrm 薄 P/Invoke: 只用 legacy KMS(GetResources/SetCrtc/PageFlip/AddFB2) 与 master 切换, 不做 atomic。 */
internal static class DrmNative
{
    public const int DrmModeConnected = 1;
    public const uint DrmModePageFlipEvent = 0x01;
    public const uint DrmModeTypePreferred = 1 << 3;
    public const uint DrmFormatXrgb8888 = 0x34325258;
    public const int DrmEventContextVersion = 2;

    private const string Lib = "libdrm.so.2";

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeModeInfo
    {
        public uint Clock;
        public ushort Hdisplay;
        public ushort HsyncStart;
        public ushort HsyncEnd;
        public ushort Htotal;
        public ushort Hskew;
        public ushort Vdisplay;
        public ushort VsyncStart;
        public ushort VsyncEnd;
        public ushort Vtotal;
        public ushort Vscan;
        public uint Vrefresh;
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeRes
    {
        public int CountFbs;
        public IntPtr Fbs;
        public int CountCrtcs;
        public IntPtr Crtcs;
        public int CountConnectors;
        public IntPtr Connectors;
        public int CountEncoders;
        public IntPtr Encoders;
        public uint MinWidth;
        public uint MaxWidth;
        public uint MinHeight;
        public uint MaxHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeConnector
    {
        public uint ConnectorId;
        public uint EncoderId;
        public uint ConnectorType;
        public uint ConnectorTypeId;
        public uint Connection;
        public uint MmWidth;
        public uint MmHeight;
        public uint Subpixel;
        public uint CountModes;
        public IntPtr Modes;
        public uint CountProps;
        public IntPtr Props;
        public IntPtr PropValues;
        public uint CountEncoders;
        public IntPtr Encoders;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrmModeEncoder
    {
        public uint EncoderId;
        public uint EncoderType;
        public uint CrtcId;
        public uint PossibleCrtcs;
        public uint PossibleClones;
    }

    /** struct drmEventContext: 只填 version 与 page_flip_handler, 由 drmHandleEvent 分发翻页完成事件。 */
    [StructLayout(LayoutKind.Sequential)]
    public struct DrmEventContext
    {
        public int Version;
        public IntPtr VblankHandlerPointer;
        public IntPtr PageFlipHandlerPointer;
        public IntPtr PageFlipHandler2Pointer;
        public IntPtr SequenceHandlerPointer;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PageFlipHandler(int fd, uint sequence, uint tvSec, uint tvUsec, IntPtr userData);

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr drmModeGetResources(int fd);

    [DllImport(Lib, SetLastError = true)]
    public static extern void drmModeFreeResources(IntPtr res);

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr drmModeGetConnector(int fd, uint connectorId);

    [DllImport(Lib, SetLastError = true)]
    public static extern void drmModeFreeConnector(IntPtr connector);

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr drmModeGetEncoder(int fd, uint encoderId);

    [DllImport(Lib, SetLastError = true)]
    public static extern void drmModeFreeEncoder(IntPtr encoder);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmModeSetCrtc(int fd, uint crtcId, uint fbId, uint x, uint y, uint[] connectors, int count, IntPtr mode);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmModeAddFB2(int fd, uint width, uint height, uint pixelFormat, uint[] handles, uint[] pitches, uint[] offsets, out uint bufId, uint flags);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmModeRmFB(int fd, uint fbId);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmModePageFlip(int fd, uint crtcId, uint fbId, uint flags, IntPtr userData);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmHandleEvent(int fd, IntPtr evctx);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmSetMaster(int fd);

    [DllImport(Lib, SetLastError = true)]
    public static extern int drmDropMaster(int fd);

    /** 从 connector 的 modes 数组按索引读出一个 mode(结构体含定长 name, 必须按元素大小定位)。 */
    public static DrmModeModeInfo ReadMode(IntPtr modes, int index)
        => Marshal.PtrToStructure<DrmModeModeInfo>(IntPtr.Add(modes, index * Marshal.SizeOf<DrmModeModeInfo>()))!;

    /** 从 uint32 数组指针按索引读一个元素(CRTC/connector id 列表)。 */
    public static uint ReadUInt32(IntPtr array, int index) => (uint)Marshal.ReadInt32(array, index * sizeof(uint));
}
