namespace Dsh.Web;

internal sealed class WebDisposable(params IDisposable?[] disposables) : IDisposable
{
    public void Dispose()
    {
        foreach (var disposable in disposables)
            disposable?.Dispose();
    }
}

internal sealed class EffectHandleDisposable(Cordis.EffectHandle handle) : IDisposable
{
    public void Dispose() => handle.Dispose();
}
