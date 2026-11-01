namespace Dsh.GitInspect;

/// <summary>Protected model callbacks cannot launch unconfined git, including repository-configured helper programs.</summary>
public sealed class GitExecutionScope : IDisposable
{
    private static readonly AsyncLocal<bool> Forbidden = new();
    private readonly bool _previous;
    private bool _disposed;

    public GitExecutionScope()
    {
        _previous = Forbidden.Value;
        Forbidden.Value = true;
    }

    public static void DemandUnconfinedExecution()
    {
        if (Forbidden.Value)
            throw new NotSupportedException("Direct git inspection cannot enforce OS confinement for repository-configured helpers while sandbox.enabled is ON. Use a confined tool subprocess instead; manual inspection remains available.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Forbidden.Value = _previous;
    }
}
