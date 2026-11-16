using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidBuild = Android.OS.Build;
using Dsh.Gui.Shared.Mobile;

namespace Dsh.Android.Services;

/**
 * 手机自身能力的 Android 实现。
 * Context / PackageManager 都是 Java 代理对象, 只在一次调用里取用, 不放进字段:
 * 长期持有会让 Java 与托管两个 GC 互相牵挂, 也是本仓库既有的约定。
 */
public sealed class AndroidPhoneDevice : IPhoneDevice
{
    public string Describe()
    {
        var context = global::Android.App.Application.Context;
        var app = context?.PackageName ?? "";
        var model = $"{AndroidBuild.Manufacturer} {AndroidBuild.Model}";
        var abi = string.Join('/', AndroidBuild.SupportedAbis ?? []);
        return $"{model}\n设备代号: {AndroidBuild.Device}\nABI: {abi}\nDSH 包名: {app}";
    }

    public IReadOnlyList<PhoneApp> ListApps(string? filter)
    {
        var context = global::Android.App.Application.Context;
        if (context is null)
            return [];
        var manager = context.PackageManager;
        if (manager is null)
            return [];
        var intent = new Intent(Intent.ActionMain);
        intent.AddCategory(Intent.CategoryLauncher);
        var resolved = manager.QueryIntentActivities(intent, PackageInfoFlags.MatchAll) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apps = new List<PhoneApp>();
        foreach (var entry in resolved)
        {
            var activity = entry.ActivityInfo;
            var package = activity?.PackageName;
            if (package is null || !seen.Add(package))
                continue;
            var label = activity!.LoadLabel(manager)?.ToString() ?? package;
            if (filter is { Length: > 0 }
                && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !package.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            apps.Add(new PhoneApp(package, label));
        }
        return [.. apps.OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    public PhoneActionResult LaunchApp(string package)
    {
        var name = package.Trim();
        if (name.Length == 0)
            return PhoneActionResult.Fail("缺少包名; 先用 list_apps 查出要启动的应用");
        var context = global::Android.App.Application.Context;
        var manager = context?.PackageManager;
        if (context is null || manager is null)
            return PhoneActionResult.Fail("应用上下文不可用");
        var intent = manager.GetLaunchIntentForPackage(name);
        if (intent is null)
            return PhoneActionResult.Fail($"没有可启动的应用 {name}; 先用 list_apps 确认包名");
        intent.AddFlags(ActivityFlags.NewTask);
        return Start(context, intent, $"已启动 {name}");
    }

    public PhoneActionResult OpenUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return PhoneActionResult.Fail("只接受 http/https 链接");
        var context = global::Android.App.Application.Context;
        if (context is null)
            return PhoneActionResult.Fail("应用上下文不可用");
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(parsed.AbsoluteUri));
        intent.AddFlags(ActivityFlags.NewTask);
        return Start(context, intent, $"已打开 {parsed.Host}");
    }

    /** Android 10 起后台直接拉起 Activity 会被系统拒绝; 这种情况下把原因如实带回给 agent。 */
    private static PhoneActionResult Start(Context context, Intent intent, string success)
    {
        try
        {
            context.StartActivity(intent);
            return PhoneActionResult.Done(success);
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.Exception or InvalidOperationException)
        {
            return PhoneActionResult.Fail($"启动失败: {error.Message}");
        }
    }
}
