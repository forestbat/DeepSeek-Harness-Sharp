namespace Dsh.GitInspect;

/// <summary>Protected model callbacks cannot launch unconfined git, including repository-configured helper programs.</summary>
public sealed class GitExecutionScope : IDisposable
{
    private static readonly AsyncLocal<Action?> Demand = new();
    private readonly Action? _previous;
    private bool _disposed;

    public GitExecutionScope(Action? demand = null)
    {
        _previous = Demand.Value;
        Demand.Value = demand ?? RefuseProtectedExecution;
    }

    public static void DemandUnconfinedExecution() => Demand.Value?.Invoke();

    private static void RefuseProtectedExecution()
        => throw new NotSupportedException("Direct git inspection cannot enforce OS confinement for repository-configured helpers while sandbox.enabled is ON. Use a confined tool subprocess instead; manual inspection remains available.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Demand.Value = _previous;
    }
}
