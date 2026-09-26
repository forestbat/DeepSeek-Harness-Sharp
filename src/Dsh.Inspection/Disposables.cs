namespace Dsh.Inspection;

internal sealed class DisposableBundle(params IDisposable[] items) : IDisposable
{
    public void Dispose()
    {
        foreach (var item in items)
            item.Dispose();
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}
