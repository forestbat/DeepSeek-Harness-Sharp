namespace Dsh.Runtime;

/// <summary>Supplies the platform lifetime and standard streams of an owned process.</summary>
public interface IChildProcessLifetime : IAsyncDisposable
{
    int Id { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task<int> Exit { get; }
    void Kill();
}
