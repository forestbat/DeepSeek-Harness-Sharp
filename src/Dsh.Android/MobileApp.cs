using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dsh.Android.Services;
using Dsh.Android.ViewModels;
using Dsh.Android.Views;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Android;

public sealed class MobileApp : Avalonia.Application
{
    private MobileShellViewModel? _shell;

    public override void Initialize()
    {
        // 手机端不组合插件宿主, Dsh.Core 的序列化上下文不会由插件目录注册; 会话事件解析要用同一份源生成上下文。
        DshJson.RegisterResolver(DshCoreJsonContext.Default);
        // 窄屏上一条长回复能占掉好几屏, 所以消息正文默认折叠, 双击展开。
        MessageViewModel.CompactByDefault = true;
        // 一帧要解析两次(读循环读成 JsonElement, 客户端再反序列化); 分开计时才知道瓶颈在帧到达还是解析。
        RemoteHostClient.HandshakeTrace = MobileLog.Info;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Android 每次重建 Activity 都会调用 MainViewFactory: 连接与消息留在同一个 ViewModel 里, 只换新视图,
        // 否则旧视图还挂在旧 root 上, 直接命中 Avalonia 的 "already has a visual parent"。
        _shell ??= new MobileShellViewModel(new MachineStore(), new PhoneToolStore(), new LocalAgentStore());
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new ShellView { DataContext = _shell };
        base.OnFrameworkInitializationCompleted();
    }
}
