using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Dsh.Android.Services;

namespace Dsh.Android;

[Activity(
    Label = "DSH",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity
{
    private const int PostNotificationsRequest = 1;

    /** 前台服务只允许在 App 可见时启动; 自动连接发生在 Activity 创建之前, 由 OnStart 补启。 */
    public static bool IsForeground { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], PostNotificationsRequest);
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsForeground = true;
        if (ConnectionStatusHub.Snapshot.Active)
            ConnectionServiceBridge.Start();
    }

    protected override void OnPause()
    {
        IsForeground = false;
        base.OnPause();
    }
}
