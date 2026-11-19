using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Dsh.Benchmarks;

/**
 * 入口。BenchmarkDotNet 的形态是独立 exe, 所以这个工程不进解决方案、不参与默认构建:
 *
 *   dotnet run -c Release --project benchmarks/Dsh.Benchmarks                                # 跑全部基准
 *   dotnet run -c Release --project benchmarks/Dsh.Benchmarks -- --filter *SessionLoad*      # 按名字过滤
 *   dotnet run -c Release --project benchmarks/Dsh.Benchmarks -- --list flat                 # 只看清单
 *   dotnet run -c Release --project benchmarks/Dsh.Benchmarks -- --report                    # 落盘体积/帧数报表
 *
 * 产物统一落在仓库根的 artifacts/benchmarks/, 与其它构建产物同处, 不污染工程目录。
 */
public static class Program
{
    public static void Main(string[] args)
    {
        // 体积、帧数这类确定性指标不进 BDN(它不显示返回值), 走独立的报表命令。
        if (args is ["--report", ..])
        {
            WriteReport.Run(Console.Out);
            return;
        }

        var config = DefaultConfig.Instance
            .AddJob(Job.Default.WithId("default"))
            .WithArtifactsPath(Path.Combine(RepositoryRoot(), "artifacts", "benchmarks"))
            // 常规基准不该被"是不是 Debug 构建"拦下来: 这个工程本身已经钉死 Release。
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }

    /** 仓库根: 从程序集位置往上找解决方案文件, 找不到就退回当前目录。 */
    internal static string RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
