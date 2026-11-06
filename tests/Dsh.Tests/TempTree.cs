namespace Dsh.Tests;

/**
 * 测试临时目录(规则 18: 一律建在项目内 artifacts020 下, 不写系统临时目录)。
 * 清理需清只读并重试: checkpoints 插件会在工作区内建 git 裸仓库, git 对象文件只读,
 * 且收尾写盘与句柄释放滞后于测试结束, 直接 Directory.Delete 会偶发失败。
 */
internal static class TempTree
{
    private const int MaxDeleteAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /** 项目内临时目录根: 与 ChatWindow* 等测试同一处, 便于统一清理与忽略(.gitignore /artifacts020)。 */
    private static readonly string Root = Path.Combine(RepositoryRoot, "artifacts020", "test-homes");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Test artifacts require a repository checkout containing DeepSeek-Harness-Sharp.slnx.");
    }
    public static string CreateDirectory(string prefix)
    {
        var directory = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }


    /** 浅目录根(仓库内 .daemon-tests): AF_UNIX 路径上限很小(macOS 104, Linux 108), test-homes 的深路径放不下套接字。 */
    private static readonly string SocketRoot = Path.Combine(RepositoryRoot, ".daemon-tests");

    /** GitHub macOS runner 的仓库检出位置; 本机仓库路径更短时, 套接字路径在本机能绑定而在 CI 上超长。 */
    private const string MacOsCiRepositoryRoot = "/Users/runner/work/DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp";

    private const int MacOsSocketPathLimit = 104;

    /** 建一个仓库内的浅目录(名字为 prefix + 6 位十六进制), 供 Unix 域套接字路径使用。 */
    public static string CreateSocketDirectory(string prefix)
    {
        var directory = Path.Combine(SocketRoot, prefix + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /** 断言仓库内的套接字路径换到 macOS CI 检出位置后仍不超过 104 字符, 让 Linux 上的测试就能发现 macOS 上的超长。 */
    public static void AssertFitsMacOsSocketLimit(string socketPath)
    {
        var onCi = $"{MacOsCiRepositoryRoot}/{Path.GetRelativePath(RepositoryRoot, socketPath)}";
        Assert.True(onCi.Length <= MacOsSocketPathLimit, $"{onCi} has {onCi.Length} characters, macOS allows {MacOsSocketPathLimit}");
    }

    /** Clears read-only files and deletes an owned tree once, propagating all cleanup failures. */
    public static void DeleteOnce(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }

    /** 清只读 + 重试删除; 句柄/写盘滞后时不抛异常(交由后续重试或忽略残留)。 */
    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        for (var attempt = 0; attempt < MaxDeleteAttempts; attempt++)
        {
            try
            {
                DeleteOnce(directory);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
