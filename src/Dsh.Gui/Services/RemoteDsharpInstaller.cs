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

    /** 部署时写入 `~/.dsharp/<rid>/.dsharp-version` 的标记: 与客户端协议版本一致才算可复用。 */
    private static string ExpectedVersion => typeof(RemoteHost.RemoteHostServer).Assembly.GetName().Version?.ToString() ?? "0";

    private const string VersionFileName = ".dsharp-version";

    /** 部署参数(来自 GUI 设置): Release 基址/版本; Proxy=本地下载代理; Via=下载位置(auto|client|server); ServerProxy=远端下载代理(空则用远端自带代理/直连)。 */
    public sealed record Options(string ReleaseBaseUrl, string Version, string? Proxy, string Via = "auto", string? ServerProxy = null);

    /** 部署结果: CommandPath 为远端 dsharp 的绝对路径(可交给启动器当 hostCommand)。 */
    public sealed record Result(string CommandPath, bool AlreadyPresent);

    /** 远端探测脚本: 依次找 PATH、~/.local/bin、~/.dsharp/RID/; 找到即输出“路径 + 版本标记”两行, 找不到打印标记。 */
    private const string ProbeCommand =
        "p=\"\"; "
        + "if command -v dsharp >/dev/null 2>&1; then p=$(command -v dsharp); "
        + "elif [ -x \"$HOME/.local/bin/dsharp\" ]; then p=\"$HOME/.local/bin/dsharp\"; "
        + "elif ls \"$HOME\"/.dsharp/*/dsharp >/dev/null 2>&1; then p=$(ls \"$HOME\"/.dsharp/*/dsharp | head -n1); fi; "
        + $"if [ -n \"$p\" ]; then echo \"$p\"; cat \"$(dirname \"$p\")/{VersionFileName}\" 2>/dev/null || echo \"\"; else echo {MissingMarker}; fi";

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
        // 先按远端真实平台判定, 再选对应脚本: 不能假设远端一定是 POSIX。
        var rid = await DetectRidAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (rid.StartsWith("win-", StringComparison.Ordinal))
            throw new NotSupportedException("远端为 Windows: 自动部署暂未实现, 请在该远程工作区设置里手动填写远端 dsharp 路径");

        var probed = Lines(await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, ProbeCommand, cancellationToken).ConfigureAwait(false));
        if (probed.Length > 0 && probed[0] != MissingMarker)
        {
            var present = probed[0];
            var version = probed.Length > 1 ? probed[1] : "";
            // 有版本标记且不一致才算过期; 无标记(旧部署)按可用处理, 不强制重装。
            if (version.Length == 0 || string.Equals(version, ExpectedVersion, StringComparison.Ordinal))
                return new Result(present, true);
            progress?.Invoke($"远端 dsharp 版本过期({version} → {ExpectedVersion}), 重新部署 …");
        }

        // server/auto: 先让远端用它自己的网络/自带代理下载(curl/wget 认 http_proxy/https_proxy 环境, 或 ServerProxy);
        // auto 失败(无下载器/无外网/404)再回退到"本地下载 + scp 上传"。
        if (options.Via is "server" or "auto")
        {
            try
            {
                progress?.Invoke($"正在让远端直接下载 {AssetName(rid)} …");
                return await DeployFromServerAsync(workspace, options, rid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (options.Via == "auto" && error is not OperationCanceledException)
            {
                progress?.Invoke($"远端直接下载不可用({error.Message}), 改由本地下发 …");
            }
        }

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

    /** 远端平台 → .NET RID(可按需扩展; 目前覆盖 glibc Linux / macOS / Windows)。 */
    public static string? RidFor(string os, string arch) => (os.Trim().ToLowerInvariant(), arch.Trim().ToLowerInvariant()) switch
    {
        ("linux", "x86_64") or ("linux", "amd64") => "linux-x64",
        ("linux", "aarch64") or ("linux", "arm64") => "linux-arm64",
        ("darwin", "x86_64") => "osx-x64",
        ("darwin", "arm64") or ("darwin", "aarch64") => "osx-arm64",
        ("windows", "x86_64") or ("windows", "amd64") => "win-x64",
        ("windows", "aarch64") or ("windows", "arm64") => "win-arm64",
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

    /** 远端直接下载并解压的脚本: 用远端自带代理(curl/wget 认 http_proxy/https_proxy 环境)或 ServerProxy; 无 curl/wget 时非零退出以触发 auto 回退。 */
    public static string BuildServerDownloadScript(Options options, string rid)
    {
        var url = AssetUrl(options, rid);
        var proxyArg = string.IsNullOrWhiteSpace(options.ServerProxy) ? "" : $"-x '{options.ServerProxy.Trim()}' ";
        var dir = $"\"$HOME/.dsharp/{rid}\"";
        return "set -e; "
            + $"mkdir -p {dir}; "
            + $"cd {dir}; "
            + $"if command -v curl >/dev/null 2>&1; then curl -fL --connect-timeout 15 --max-time 900 --retry 2 {proxyArg}-o dsharp.tar.gz '{url}'; "
            + $"elif command -v wget >/dev/null 2>&1; then wget --timeout=15 --tries=2 -O dsharp.tar.gz '{url}'; "
            + "else echo __NO_DOWNLOADER__ >&2; exit 42; fi; "
            + "tar -xf dsharp.tar.gz; "
            + "chmod +x dsharp; "
            + $"printf '%s' '{ExpectedVersion}' > {VersionFileName}; "
            + "rm -f dsharp.tar.gz; "
            + "pwd";
    }

    /** 远端平台识别: 先 `uname`(POSIX), 失败再试 PowerShell / cmd(Windows 远端默认 shell 多为 cmd)。 */
    private static async Task<string> DetectRidAsync(SshWorkspace workspace, CancellationToken cancellationToken)
    {
        if (await TryRunAsync(workspace, "uname -s; uname -m", cancellationToken).ConfigureAwait(false) is { } uname)
        {
            var parts = Lines(uname);
            if (parts.Length >= 2 && RidFor(parts[0], parts[1]) is { } rid)
                return rid;
        }

        if (await TryRunAsync(workspace, "powershell -NoProfile -NonInteractive -Command \"$env:PROCESSOR_ARCHITECTURE\"", cancellationToken).ConfigureAwait(false) is { } powershell
            && RidFor("windows", LastLine(powershell)) is { } psRid)
        {
            return psRid;
        }

        if (await TryRunAsync(workspace, "cmd /c echo %PROCESSOR_ARCHITECTURE%", cancellationToken).ConfigureAwait(false) is { } cmd
            && RidFor("windows", LastLine(cmd)) is { } cmdRid)
        {
            return cmdRid;
        }

        throw new NotSupportedException("无法识别远端平台(既非 POSIX uname, 也非 Windows)");
    }

    private static async Task<string?> TryRunAsync(SshWorkspace workspace, string command, CancellationToken cancellationToken)
    {
        try
        {
            return await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string[] Lines(string output)
        => output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string LastLine(string output) => Lines(output).LastOrDefault() ?? "";

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
            + $"printf '%s' '{ExpectedVersion}' > \"$HOME/.dsharp/{rid}/{VersionFileName}\"; "
            + $"rm -f \"$HOME/{archiveName}\"; "
            + $"cd \"$HOME/.dsharp/{rid}\" && pwd";
        var dir = (await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, script, cancellationToken).ConfigureAwait(false)).Trim();
        return dir.Length == 0
            ? throw new IOException("远端部署完成但未返回安装目录")
            : new Result($"{dir.TrimEnd('/')}/dsharp", false);
    }

    private static async Task<Result> DeployFromServerAsync(SshWorkspace workspace, Options options, string rid, CancellationToken cancellationToken)
    {
        var dir = (await SshRemoteWorkspaceLauncher.RunRemoteAsync(workspace, BuildServerDownloadScript(options, rid), cancellationToken).ConfigureAwait(false)).Trim();
        return dir.Length == 0
            ? throw new IOException("远端下载完成但未返回安装目录")
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
