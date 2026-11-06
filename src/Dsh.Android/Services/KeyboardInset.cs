namespace Dsh.Android.Services;

/**
 * 输入法盖住窗口的高度(与 Avalonia 同一套设备无关单位)。
 * adjustResize 在 edge-to-edge 窗口上不给 Avalonia 缩小客户区, 所以由 Activity 直接读 IME inset 广播过来,
 * 界面把底部内容抬同样高度, 用户才能一边打字一边看到自己写的内容。
 */
public static class KeyboardInset
{
    private static double _bottom;

    public static event Action<double>? Changed;

    public static double Bottom => _bottom;

    public static void Report(double bottom)
    {
        if (double.IsNaN(bottom) || bottom < 0)
            bottom = 0;
        if (Math.Abs(bottom - _bottom) < 0.5)
            return;
        _bottom = bottom;
        Changed?.Invoke(bottom);
    }
}
