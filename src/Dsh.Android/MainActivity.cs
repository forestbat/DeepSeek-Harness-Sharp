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

    /** 前台服务只允许在 App 可交互时启动; 自动连接发生在 Activity 创建之前, 由 OnResume 补启。 */
    public static bool IsForeground { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], PostNotificationsRequest);
        HandleShare(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShare(intent);
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
            OfferImage(uri, intent.Type);
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
            input.CopyTo(buffer);
            if (buffer.Length == 0 || buffer.Length > ImageAttachments.MaxBytes)
            {
                ShareInbox.Offer(ShareItem.ForError($"分享的图片为空或超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB"));
                return;
            }
            var bytes = buffer.ToArray();
            var mediaType = ImageAttachments.Sniff(bytes)
                ?? ImageAttachments.MediaTypeForExtension(Path.GetExtension(DisplayName(uri)))
                ?? declaredType
                ?? "image/png";
            ShareInbox.Offer(ShareItem.ForImage(DisplayName(uri), mediaType, bytes));
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
