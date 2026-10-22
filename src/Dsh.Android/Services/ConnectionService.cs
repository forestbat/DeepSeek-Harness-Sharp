using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Dsh.Android.Services;

/**
 * 连接期间的前台服务: 让 App 退到后台后连接、心跳与重连继续跑, 并把状态放进常驻通知。
 * 只在用户主动断开时停止, 系统重启不会自动拉起(NotSticky) —— 恢复连接由 App 启动时决定。
 */
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ConnectionService : Service
{
    public const string ChannelId = "dsh-connection";
    private const int NotificationId = 1;

    private NotificationManager? _notifications;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _notifications = GetSystemService(NotificationService) as NotificationManager;
        if (_notifications is not null)
        {
            var channel = new NotificationChannel(ChannelId, "连接状态", NotificationImportance.Low)
            {
                Description = "DSH 与桌面 host 的连接状态",
            };
            _notifications.CreateNotificationChannel(channel);
        }
        ConnectionStatusHub.Changed += OnStatusChanged;
        try
        {
            StartForeground(NotificationId, Build(ConnectionStatusHub.Snapshot.Detail));
        }
        catch (Exception error) when (error is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException)
        {
            // 系统拒绝了前台化(例如锁屏后被拉起): 不留在后台跑一个随时会被杀的普通服务, 等下次可见再起。
            StopSelf();
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        OnStatusChanged();
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        ConnectionStatusHub.Changed -= OnStatusChanged;
        base.OnDestroy();
    }

    private void OnStatusChanged()
    {
        var (detail, active) = ConnectionStatusHub.Snapshot;
        _notifications?.Notify(NotificationId, Build(detail));
        if (!active)
            StopSelf();
    }

    private Notification Build(string detail)
    {
        var openApp = PendingIntent.GetActivity(
            this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("DSH")
            .SetContentText(detail)
            .SetSmallIcon(Resource.Drawable.ic_dsh)
            .SetOngoing(true)
            .SetContentIntent(openApp)
            .Build() ?? throw new InvalidOperationException("notification builder returned null");
    }
}

/** 唯一从前台代码碰 Android 服务 API 的地方: 只在连接开始/结束时调用, 不在热路径上。 */
public static class ConnectionServiceBridge
{
    /** 由 MainActivity 在可交互时调用: Android 12+ 不允许后台随意启动前台服务。 */
    public static bool Start()
    {
        var context = global::Android.App.Application.Context;
        try
        {
            context.StartForegroundService(new Intent(context, typeof(ConnectionService)));
            return true;
        }
        catch (Exception error) when (error is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException)
        {
            return false;
        }
    }

    public static void Publish(string detail) => ConnectionStatusHub.Publish(detail, active: true);

    public static void Stop()
    {
        ConnectionStatusHub.Publish("未连接", active: false);
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(ConnectionService)));
    }
}
