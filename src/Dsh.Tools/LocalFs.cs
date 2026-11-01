using System.Text;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Transport;

namespace Dsh.Tools;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record LocalFsConfig
{
    public string? Cwd { get; init; }
}

public sealed class LocalFsService : Service
{
    public const string ServiceName = "fs";

    public LocalFsService(Context ctx, LocalFsConfig? config = null) : base(ctx, ServiceName)
    {
        Cwd = Path.GetFullPath(config?.Cwd ?? Environment.CurrentDirectory);
    }

    public string Cwd { get; }

    // 服务实例的 provide 生命周期挂在调用方 fiber 上,此处无额外资源需要释放。
    public static IDisposable Register(Context ctx, string? cwd = null)
    {
        _ = new LocalFsService(ctx, new LocalFsConfig { Cwd = cwd });
        return new CompositeDisposable();
    }

    public object Resolve(string path, IDictionary<string, object?>? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("fs.resolve: path must be a non-empty string");
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        var baseDir = options is not null && options.TryGetValue("cwd", out var cwd) ? cwd as string : null;
        return Path.GetFullPath(string.IsNullOrEmpty(baseDir) ? Path.Combine(Cwd, path) : Path.Combine(baseDir, path));
    }

    private CancellationToken Token(object? signal)
        => signal is CancellationToken token ? token : SettingsMutationScope.Current?.Context.Signal ?? CancellationToken.None;

    private async Task<Action<PinnedFileTarget>> Authorize(string path, string intent, CancellationToken signal)
    {
        if (SettingsMutationScope.Current is { } scope)
            return await SafetyFileAccess.AuthorizeAsync(path, intent, scope.Context, signal).ConfigureAwait(false);
        var safety = Ctx.Get<ISafetyBoundary>(ISafetyBoundary.ServiceName, false);
        return safety is null ? static _ => { }
            : await safety.AuthorizeFileAsync(null, path, intent, signal, lifecycleOwner: ServiceName).ConfigureAwait(false);
    }

    public async Task<object?> Stat(string path, object? signal = null)
    {
        var full = Path.GetFullPath(path);
        var token = Token(signal);
        var authorize = await Authorize(full, "read", token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (File.Exists(full))
        {
            await using var file = PinnedFileAccess.Open(full, PinnedFileMode.Read, authorize);
            return new Dictionary<string, object?>
            {
                ["type"] = "file", ["isFile"] = true, ["isDirectory"] = false,
                ["size"] = file.Stream.Length,
                ["mtimeMs"] = new DateTimeOffset(File.GetLastWriteTimeUtc(file.Stream.SafeFileHandle)).ToUnixTimeMilliseconds(),
            };
        }
        if (!Directory.Exists(full)) return null;
        PinnedFileAccess.DemandDirectory(full, authorize);
        return new Dictionary<string, object?> { ["type"] = "directory", ["isFile"] = false, ["isDirectory"] = true };
    }

    public async Task<object> ReadText(string path, object? signal = null)
    {
        var token = Token(signal);
        var full = Path.GetFullPath(path);
        var authorize = await Authorize(full, "read", token).ConfigureAwait(false);
        await using var file = PinnedFileAccess.Open(full, PinnedFileMode.Read, authorize);
        using var reader = new StreamReader(file.Stream, new UTF8Encoding(false, false));
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    public async Task<object> WriteText(string path, string content, object? signal = null)
    {
        var token = Token(signal);
        var full = Path.GetFullPath(path);
        if (SettingsMutationScope.Current is { } scope)
        {
            var before = File.Exists(full) ? await SettingsMutationScope.ReadTextAsync(full, scope.Context).ConfigureAwait(false) : "";
            await SettingsMutationScope.WriteTextAsync(full, before, content, scope.Context).ConfigureAwait(false);
        }
        else
        {
            var authorize = await Authorize(full, "write", token).ConfigureAwait(false);
            await using var file = PinnedFileAccess.Open(full, PinnedFileMode.Replace, authorize);
            await file.Stream.WriteAsync(Encoding.UTF8.GetBytes(content), token).ConfigureAwait(false);
            await file.CompleteAsync(token).ConfigureAwait(false);
        }
        return new Dictionary<string, object?> { ["path"] = full };
    }

    public async Task<object> Exists(string path, object? signal = null)
        => await Stat(path, signal).ConfigureAwait(false) is not null;
}
