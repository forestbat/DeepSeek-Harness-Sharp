namespace Dsh.Android.Services;

/**
 * UI 与前台服务共享的最近一次连接状态。
 * 只保存字符串与布尔值, 不持有 Java 对象; 服务在后台线程读它更新通知。
 */
public static class ConnectionStatusHub
{
    private static readonly Lock Gate = new();
    private static string _detail = "未连接";
    private static bool _active;

    public static event Action? Changed;

    public static (string Detail, bool Active) Snapshot
    {
        get
        {
            lock (Gate)
                return (_detail, _active);
        }
    }

    public static void Publish(string detail, bool active)
    {
        lock (Gate)
        {
            _detail = detail;
            _active = active;
        }
        Changed?.Invoke();
    }
}
