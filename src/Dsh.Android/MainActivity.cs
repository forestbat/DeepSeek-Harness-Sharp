using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Dsh.Android.Services;
using Dsh.Llm;

namespace Dsh.Android;

[Activity(
    Label = "DSH",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "image/*")]
public class MainActivity : AvaloniaMainActivity
{
    private const int PostNotificationsRequest = 1;
    private const int ShareImageReadChunkBytes = 16 * 1024;

    /** 前台服务只允许在 App 可交互时启动; 自动连接发生在 Activity 创建之前, 由 OnResume 补启。 */
    public static bool IsForeground { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.DecorView?.SetOnApplyWindowInsetsListener(new ImeInsetListener());
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], PostNotificationsRequest);
        HandleShare(Intent);
    }

    /** 窗口是 edge-to-edge 的, adjustResize 不会给 Avalonia 缩小客户区; 直接读 IME inset 交给界面自己抬底部。 */
    private sealed class ImeInsetListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets? OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            if (insets is not null && Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var density = view?.Resources?.DisplayMetrics?.Density ?? 1f;
                var bottom = insets.GetInsets(WindowInsets.Type.Ime()).Bottom / density;
                KeyboardInset.Report(bottom);
            }
            return insets;
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShare(intent);
    }

    /**
     * 系统返回热键: 先让界面按页面层级退一级, 退无可退才交回系统退出 App。
     * 不拦截的话在任何页面按返回都会直接关掉程序。
     */
    public override void OnBackPressed()
    {
        if (BackNavigation.TryGoBack())
            return;
        base.OnBackPressed();
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

    /** 分享只接受单条: 一段文字或一张图; 多图分享走同一入口会分多次处理, 这里不排队多张。 */
    private void HandleShare(Intent? intent)
    {
        if (intent is null || intent.Action != Intent.ActionSend)
            return;
        if (intent.GetStringExtra(Intent.ExtraText) is { Length: > 0 } text)
            ShareInbox.Offer(ShareItem.ForText(text));
        var stream = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri)))
            : intent.GetParcelableExtra(Intent.ExtraStream);
        if (stream is global::Android.Net.Uri { Scheme: not null } uri)
        {
            var declaredType = intent.Type;
            _ = Task.Run(() => OfferImage(uri, declaredType));
        }
    }

    private void OfferImage(global::Android.Net.Uri uri, string? declaredType)
    {
        try
        {
            using var input = ContentResolver?.OpenInputStream(uri);
            if (input is null)
            {
                ShareInbox.Offer(ShareItem.ForError("无法读取分享的图片"));
                return;
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[ShareImageReadChunkBytes];
            while (true)
            {
                var remaining = ImageAttachments.MaxBytes - (int)buffer.Length;
                var toRead = Math.Min(chunk.Length, remaining + 1);
                var read = input.Read(chunk, 0, toRead);
                if (read <= 0)
                    break;
                if (buffer.Length + read > ImageAttachments.MaxBytes)
                {
                    ShareInbox.Offer(ShareItem.ForError($"分享的图片为空或超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB"));
                    return;
                }
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0)
            {
                ShareInbox.Offer(ShareItem.ForError($"分享的图片为空或超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB"));
                return;
            }
            var bytes = buffer.ToArray();
            var name = DisplayName(uri);
            var mediaType = ImageAttachments.Sniff(bytes)
                ?? ImageAttachments.MediaTypeForExtension(Path.GetExtension(name))
                ?? declaredType
                ?? "image/png";
            ShareInbox.Offer(ShareItem.ForImage(name, mediaType, bytes));
        }
        catch (Exception error)
        {
            ShareInbox.Offer(ShareItem.ForError($"读取分享的图片失败: {error.Message}"));
        }
    }

    private string DisplayName(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, [global::Android.Provider.IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
                if (index >= 0 && cursor.GetString(index) is { Length: > 0 } name)
                    return name;
            }
        }
        catch (Exception error) when (error is Java.Lang.Exception or InvalidOperationException)
        {
        }
        return uri.LastPathSegment ?? "分享的图片";
    }
}
