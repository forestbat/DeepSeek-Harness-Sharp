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
    public override void Initialize()
    {
        // 手机端不组合插件宿主, Dsh.Core 的序列化上下文不会由插件目录注册; 会话事件解析要用同一份源生成上下文。
        DshJson.RegisterResolver(DshCoreJsonContext.Default);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new ShellView
            {
                DataContext = new MobileShellViewModel(new EndpointStore()),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
