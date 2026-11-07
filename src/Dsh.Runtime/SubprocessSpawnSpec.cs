namespace Dsh.Runtime;

public sealed record SubprocessCollect(int MaxBytes, int? SpillMaxBytes);

public sealed record SubprocessSpawnSpec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public required string Cwd { get; init; }
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
    public SubprocessCollect Stdout { get; init; } = new(64 * 1024, 64 * 1024 * 1024);
    public SubprocessCollect Stderr { get; init; } = new(64 * 1024, 64 * 1024 * 1024);
    // Background processes never inherit a terminal; only protocol callers keep writable stdin, others receive EOF immediately.
    public bool KeepStandardInputOpen { get; init; }
    public CancellationToken Signal { get; init; }
    public bool ProtocolOutput { get; init; }
    public IReadOnlyList<string> AdditionalWritablePaths { get; init; } = [];
    public IReadOnlyList<string> AdditionalReadOnlyPaths { get; init; } = [];
}
