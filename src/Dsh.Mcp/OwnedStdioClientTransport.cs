using Dsh.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dsh.Mcp;

/// <summary>The MCP SDK owns framing only; SubprocessService owns the isolated process and stderr.</summary>
internal sealed class OwnedStdioClientTransport(SubprocessService subprocess, McpServerConfig config, string cwd)
    : IClientTransport, IAsyncDisposable
{
    private SubprocessHandle? _process;
    private int _terminal;

    public string Name => config.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _terminal) != 0)
            throw new IOException("The owned MCP process is unavailable; it cannot be restarted.");
        _process = await subprocess.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = [config.Command!, .. config.Arguments ?? []],
            Cwd = cwd,
            ModelExecution = true,
            ExecutionKind = SubprocessExecutionKind.Mcp,
            McpLifecycleOwner = config.Name,
            ProtocolOutput = true,
            KeepStandardInputOpen = true,
            Stderr = new SubprocessCollect(64 * 1024, null),
            Signal = cancellationToken,
        });
        try
        {
            ThrowIfUnavailable();
            return await new StreamClientTransport(_process.StandardInput.BaseStream, _process.StandardOutput)
                .ConnectAsync(cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _terminal) == 0 && Volatile.Read(ref _process) is { } process && !process.Done.IsCompleted)
            return;
        Terminate();
        throw new IOException("The owned MCP process is unavailable; the operation was not replayed.");
    }

    public void Terminate()
    {
        // Close admission before killing: an exiting process must never accept a later owned call.
        Interlocked.Exchange(ref _terminal, 1);
        Volatile.Read(ref _process)?.Terminate();
    }

    public async ValueTask DisposeAsync()
    {
        Terminate();
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
            return;
        // Spawn may publish the process between closing admission and taking ownership above.
        process.Terminate();
        try
        {
            await process.Done.ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }
}
