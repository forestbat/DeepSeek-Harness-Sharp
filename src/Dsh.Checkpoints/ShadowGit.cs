using System.Diagnostics;

namespace Dsh.Checkpoints;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

public sealed class ShadowGit
{
    private static readonly string[] BaseConfig =
    [
        "-c", "user.name=DSH Checkpoints",
        "-c", "user.email=checkpoints@dsh.local",
        "-c", "commit.gpgsign=false",
        "-c", "core.autocrlf=false",
        // 存储键含完整工作区路径, 深路径下 git 对象文件会超过 Windows MAX_PATH; 开启长路径支持。
        "-c", "core.longpaths=true",
    ];

    private readonly string _gitDir;
    private readonly string _workTree;

    public ShadowGit(string gitDir, string workTree)
    {
        _gitDir = Path.GetFullPath(gitDir);
        _workTree = Path.GetFullPath(workTree);
    }

    public string GitDir => _gitDir;

    public string WorkTree => _workTree;

    private string[] Arguments(params string[] args)
    {
        string[] prefix = [$"--git-dir={_gitDir}", $"--work-tree={_workTree}", .. BaseConfig];
        return [.. prefix, .. args];
    }

    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken signal = default)
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(_workTree) ? _workTree : _gitDir,
        };
        foreach (var arg in Arguments([.. args]))
            info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_DIR"] = _gitDir;
        info.Environment["GIT_WORK_TREE"] = _workTree;
        using var process = new Process();
        process.StartInfo = info;
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(signal);
        var errorTask = process.StandardError.ReadToEndAsync(signal);
        await process.WaitForExitAsync(signal);
        return new GitResult(process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
    }

    public async Task InitAsync(CancellationToken signal = default)
    {
        Directory.CreateDirectory(_gitDir);
        var init = await RunAsync(["init", "--quiet"], signal);
        if (!init.Ok)
            throw new InvalidOperationException($"git init failed: {init.Error}");
    }

    public async Task<string> WriteTreeAsync(CancellationToken signal = default)
    {
        var result = await RunAsync(["write-tree"], signal);
        if (!result.Ok)
            throw new InvalidOperationException($"git write-tree failed: {result.Error}");
        return result.Output;
    }

    public async Task<string> StageTreeAsync(CancellationToken signal = default)
    {
        var stage = await RunAsync(["add", "-A", "--", "."], signal);
        if (!stage.Ok)
            throw new InvalidOperationException($"git add failed: {stage.Error}");
        return await WriteTreeAsync(signal);
    }

    public async Task<string> CommitTreeAsync(string tree, string message, CancellationToken signal = default)
    {
        var commit = await RunAsync(["commit-tree", tree, "-m", message], signal);
        if (!commit.Ok)
            throw new InvalidOperationException($"git commit-tree failed: {commit.Error}");
        var sha = commit.Output;
        var reference = await RunAsync(["update-ref", $"refs/checkpoints/{sha}", sha], signal);
        if (!reference.Ok)
            throw new InvalidOperationException($"git update-ref failed: {reference.Error}");
        return sha;
    }

    public async Task<string?> TreeOfAsync(string commit, CancellationToken signal = default)
    {
        var result = await RunAsync(["rev-parse", $"{commit}^{{tree}}"], signal);
        return result.Ok ? result.Output : null;
    }

    public async Task<IReadOnlyList<string>> ListTrackedFilesAsync(string commit, CancellationToken signal = default)
    {
        var result = await RunAsync(["ls-tree", "-r", "--name-only", commit], signal);
        if (!result.Ok)
            throw new InvalidOperationException($"git ls-tree failed: {result.Error}");
        return SplitLines(result.Output);
    }

    public async Task<IReadOnlyList<string>> ListIndexFilesAsync(CancellationToken signal = default)
    {
        var result = await RunAsync(["ls-files"], signal);
        if (!result.Ok)
            throw new InvalidOperationException($"git ls-files failed: {result.Error}");
        return SplitLines(result.Output);
    }

    public async Task RestoreAsync(string commit, CancellationToken signal = default)
    {
        var stage = await RunAsync(["add", "-A", "--", "."], signal);
        if (!stage.Ok)
            throw new InvalidOperationException($"git add failed: {stage.Error}");
        var target = (await ListTrackedFilesAsync(commit, signal)).ToHashSet(StringComparer.Ordinal);
        foreach (var path in await ListIndexFilesAsync(signal))
        {
            if (target.Contains(path))
                continue;
            var fullPath = Path.GetFullPath(Path.Combine(_workTree, path));
            if (!fullPath.StartsWith(_workTree + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        var checkout = await RunAsync(["checkout", commit, "--", "."], signal);
        if (!checkout.Ok)
            throw new InvalidOperationException($"git checkout failed: {checkout.Error}");
    }

    public async Task DropAsync(IEnumerable<string> commits, CancellationToken signal = default)
    {
        var dropped = false;
        foreach (var commit in commits)
        {
            var result = await RunAsync(["update-ref", "-d", $"refs/checkpoints/{commit}"], signal);
            dropped |= result.Ok;
        }
        if (!dropped)
            return;
        await RunAsync(["gc", "--prune=now", "--quiet"], signal);
    }

    public async Task<long> RepositoryBytesAsync(CancellationToken signal = default)
    {
        if (!Directory.Exists(_gitDir))
            return 0;
        return await Task.Run(() =>
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_gitDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }
            return total;
        }, signal);
    }

    private static IReadOnlyList<string> SplitLines(string text)
        => text.Length == 0 ? [] : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
