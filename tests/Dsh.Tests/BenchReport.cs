using System.Text;

namespace Dsh.Tests;

/** 压测报告统一写到仓库 artifacts/bench 下, 供人工/CI 归档。 */
internal static class BenchReport
{
    internal static void Write(string fileName, StringBuilder report)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/bench");
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, report.ToString());
        Assert.True(File.Exists(path));
    }
}
