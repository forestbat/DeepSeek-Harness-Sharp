using Cordis;

namespace Dsh.Boot;

public sealed record PluginEntrypointOptions(
    HarnessHome Home,
    string Cwd,
    string? Config,
    IReadOnlyList<Dictionary<string, object?>>? Patches);

public static class PluginEntrypointRegistry
{
    private static readonly Dictionary<string, Func<HarnessApp, PluginEntrypointOptions, CancellationToken, Task<int>>> Registered = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static void Register(string name, Func<HarnessApp, PluginEntrypointOptions, CancellationToken, Task<int>> runner)
    {
        lock (Gate)
        {
            Registered[name] = runner;
        }
    }

    public static async Task<int> RunAsync(string name, HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken = default)
    {
        Func<HarnessApp, PluginEntrypointOptions, CancellationToken, Task<int>> runner;
        lock (Gate)
        {
            if (!Registered.TryGetValue(name, out runner!))
                throw new CordisException("UNKNOWN_ENTRYPOINT", $"unknown plugin entrypoint '{name}'");
        }

        return await runner(app, options, cancellationToken);
    }
}