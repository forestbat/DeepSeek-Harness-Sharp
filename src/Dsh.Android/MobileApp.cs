using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dsh.Android.Services;
using Dsh.Android.ViewModels;
using Dsh.Android.Views;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Android;

public sealed class MobileApp : Avalonia.Application
{
    private MobileShellViewModel? _shell;

    public override void Initialize()
    {
        // 手机端不组合插件宿主, Dsh.Core 的序列化上下文不会由插件目录注册; 会话事件解析要用同一份源生成上下文。
        DshJson.RegisterResolver(DshCoreJsonContext.Default);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Android 每次重建 Activity 都会调用 MainViewFactory: 连接与消息留在同一个 ViewModel 里, 只换新视图,
        // 否则旧视图还挂在旧 root 上, 直接命中 Avalonia 的 "already has a visual parent"。
        _shell ??= new MobileShellViewModel(new EndpointStore());
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new ShellView { DataContext = _shell };
        base.OnFrameworkInitializationCompleted();
    }
}
