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

    /** 项目内临时目录根: 与 ChatWindow* 等测试同一处, 便于统一清理与忽略(.gitignore /artifacts020)。 */
    private static readonly string Root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes"));

    public static string CreateDirectory(string prefix)
    {
        var directory = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
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
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
