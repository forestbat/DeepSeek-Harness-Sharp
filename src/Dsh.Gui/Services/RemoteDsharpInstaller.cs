using System.Collections.Concurrent;
using System.Net;

namespace Dsh.Gui.Services;

/**
 * 远端 dsharp 自动部署(仿 VS Code Remote-SSH 的 VS Code Server):
 * 远端没有 dsharp 时, 取对应平台的 Release 构建上传, 解到 `~/.dsharp/RID/`, 之后按绝对路径运行。
 * 不注册任何 PATH 命令(与 VS Code 一致): GUI 直接用 `~/.dsharp/RID/dsharp host --serve --stdio` 起宿主。
 */
public static class RemoteDsharpInstaller
{
    private const string MissingMarker = "__DSHARP_MISSING__";

    /** 部署参数(来自 GUI 设置): Release 基址/版本/下载代理。 */
    public sealed record Options(string ReleaseBaseUrl, string Version, string? Proxy);

    /** 部署结果: CommandPath 为远端 dsharp 的绝对路径(可交给启动器当 hostCommand)。 */
    public sealed record Result(string CommandPath, bool AlreadyPresent);

    /** 远端探测脚本: 依次找 PATH、~/.dsharp/RID/、~/.local/bin; 找不到打印标记。 */
    private const string ProbeCommand =
        "if command -v dsharp >/dev/null 2>&1; then command -v dsharp; "
        + "elif [ -x \"$HOME/.local/bin/dsharp\" ]; then echo \"$HOME/.local/bin/dsharp\"; "
        + "elif ls \"$HOME\"/.dsharp/*/dsharp >/dev/null 2>&1; then ls \"$HOME\"/.dsharp/*/dsharp | head -n1; "
        + $"else echo {MissingMarker}; fi";

    /**
     * 确保远端有可用的 dsharp: 有就返回其路径, 没有就下载并部署对应平台构建。
     * archiveFactory 可注入测试用的本地归档; 为空则从 GitHub Release 下载。
     */
    public static async Task<Result> EnsureAsync(
        SshWorkspace workspace,
        Options options,
        Action<string>? progress,
        CancellationToken cancellationToken,
        Func<Options, string, CancellationToken, Task<string>>? archiveFactory = null)
    {
        var found = (await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, ProbeCommand, cancellationToken).ConfigureAwait(false)).Trim();
        if (found.Length > 0 && found != MissingMarker)
            return new Result(found, true);

        var rid = await DetectRidAsync(workspace, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"远端缺少 dsharp, 正在准备 {AssetName(rid)} …");
        var archive = await (archiveFactory ?? DownloadAsync)(options, rid, cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Invoke($"正在上传并部署 dsharp({rid})到 ~/.dsharp/ …");
            return await DeployAsync(workspace, archive, rid, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archive);
        }
    }

    /** 远端平台 → .NET RID(仅支持 glibc Linux 与 macOS)。 */
    public static string? RidFor(string os, string arch) => (os.Trim().ToLowerInvariant(), arch.Trim().ToLowerInvariant()) switch
    {
        ("linux", "x86_64") or ("linux", "amd64") => "linux-x64",
        ("linux", "aarch64") or ("linux", "arm64") => "linux-arm64",
        ("darwin", "x86_64") => "osx-x64",
        ("darwin", "arm64") or ("darwin", "aarch64") => "osx-arm64",
        _ => null,
    };

    public static string AssetName(string rid) => $"dsharp-{rid}.tar.gz";

    /** Release 资产 URL: latest 或指定版本(自动补 v 前缀)。 */
    public static string AssetUrl(Options options, string rid)
    {
        var asset = AssetName(rid);
        var baseUrl = options.ReleaseBaseUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(options.Version) || options.Version == "latest")
            return $"{baseUrl}/latest/download/{asset}";
        var tag = options.Version.StartsWith('v') ? options.Version : $"v{options.Version}";
        return $"{baseUrl}/download/{tag}/{asset}";
    }

    private static async Task<string> DetectRidAsync(SshWorkspace workspace, CancellationToken cancellationToken)
    {
        var output = await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, "uname -s; uname -m", cancellationToken).ConfigureAwait(false);
        var parts = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new IOException($"无法识别远端平台(期望 uname 两行): {output.Trim()}");
        return RidFor(parts[0], parts[1]) ?? throw new NotSupportedException($"暂不支持该远端平台: {parts[0]} {parts[1]}");
    }

    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    private static HttpClient ClientFor(string? proxy)
        => Clients.GetOrAdd(proxy ?? "", key =>
        {
            var handler = new HttpClientHandler();
            if (key.Length > 0)
                handler.Proxy = new WebProxy(key);
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(10);
            return client;
        });

    private static async Task<string> DownloadAsync(Options options, string rid, CancellationToken cancellationToken)
    {
        var url = AssetUrl(options, rid);
        var bytes = await ClientFor(options.Proxy).GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(Path.GetTempPath(), $"dsharp-{rid}-{Guid.NewGuid():N}.tar.gz");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static async Task<Result> DeployAsync(SshWorkspace workspace, string archive, string rid, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var archiveName = $".dsharp-upload-{token}.tar.gz";
        await SshRemoteWorkspaceLauncher.UploadAsync(workspace, archive, archiveName, cancellationToken).ConfigureAwait(false);

        // 远端解压到 ~/.dsharp/<rid>/(归档根即发布输出), 打印解压后 dsharp 的绝对路径。tar -xf 自动识别 gzip。
        var script =
            "set -e; "
            + $"mkdir -p \"$HOME/.dsharp/{rid}\"; "
            + $"tar -xf \"$HOME/{archiveName}\" -C \"$HOME/.dsharp/{rid}\"; "
            + $"chmod +x \"$HOME/.dsharp/{rid}/dsharp\"; "
            + $"rm -f \"$HOME/{archiveName}\"; "
            + $"cd \"$HOME/.dsharp/{rid}\" && pwd";
        var dir = (await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, script, cancellationToken).ConfigureAwait(false)).Trim();
        return dir.Length == 0
            ? throw new IOException("远端部署完成但未返回安装目录")
            : new Result($"{dir.TrimEnd('/')}/dsharp", false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
