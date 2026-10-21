using Dsh.Lsp.Catalog;

namespace Dsh.Lsp.Client;

public static class LspClientFactory
{
    public static async Task<LspClientSession> StartAsync(
        LanguageServerProfile profile,
        LspClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var launch in profile.Servers)
        {
            if (!launch.IsAvailable())
            {
                failures.Add($"{launch.Executable}: not found. {launch.InstallHint}");
                continue;
            }

            LspStartedServer? started = null;
            try
            {
                started = LspServerProcess.Start(launch, options.RootPath, options.RequestTimeout);
                var session = new LspClientSession(started.Connection, options, started.StandardError);
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add($"{launch.Executable}: {error.Message}");
                if (started is not null)
                    await started.Connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        throw new LspServerLaunchException(
            $"no usable language server for {profile.DisplayName}. {string.Join(" | ", failures)}");
    }
}
