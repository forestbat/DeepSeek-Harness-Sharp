namespace Dsh.Android.Services;

/**
 * 系统返回热键的落点。
 * 界面按当前所在页面注册一个"返回上一级"处理器: 能退一级就退一级并吃掉按键,
 * 已经是最外层才交还给系统退出 App —— 否则在任何页面按返回都会直接关掉程序。
 */
public static class BackNavigation
{
    private static readonly Lock Gate = new();

    private static Func<bool>? _handler;

    /** 界面挂载时注册; 返回 false 表示"已经退无可退", 交回系统处理。 */
    public static IDisposable Register(Func<bool> handler)
    {
        lock (Gate)
            _handler = handler;
        return new Registration(handler);
    }

    /** 交给系统前先问界面能不能退一级。 */
    public static bool TryGoBack()
    {
        Func<bool>? handler;
        lock (Gate)
            handler = _handler;
        if (handler is null)
            return false;
        try
        {
            return handler();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class Registration(Func<bool> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                if (ReferenceEquals(_handler, handler))
                    _handler = null;
            }
        }
    }
}