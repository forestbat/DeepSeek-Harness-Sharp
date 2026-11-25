using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 用户可感知的打开代价: 进程已经热了(GUI 在跑), 点开一条本进程没打开过的会话。
 *
 * 只量用户在等的两段: chat_ms(宿主读日志 + 分页传给客户端) 与 project_ms(整段历史投影成界面模型)。
 * 不用任何新打点, 于是改动前后的两棵树都能跑, 可以直接比。
 */
[Collection("Bench")]
[Trait("Category", "Bench")]
public sealed class SessionOpenUserPerceivedTests
{
    private const string LogFileName = "session.jsonl.zstd";
    private const string SurfacePattern = "*.surface";

    [Fact(Explicit = true)]
    public async Task WarmProcessOpenOfTheLargestSessions() => await HeadlessGui.RunAsync(async () =>
    {
        var top = TopSessions();
        Assert.NotEmpty(top);
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var rows = new StringBuilder("session,logBytes,chat_ms,project_ms,total_ms,messages").AppendLine();
        foreach (var (name, directory, bytes) in top)
        {
            var copy = Stage(environment, name, directory);
            var sessionId = SessionId.Create(Path.GetFileName(copy));
            foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(copy)!, SurfacePattern))
                File.Delete(sidecar);

            var clock = Stopwatch.StartNew();
            var chat = await environment.OpenChatAsync(sessionId);
            var chatMs = clock.ElapsedMilliseconds;
            clock.Restart();
            var viewModel = new MainViewModel(environment.App, chat);
            var projectMs = clock.ElapsedMilliseconds;
            var messages = viewModel.Messages.Count;
            rows.Append(name).Append(',').Append(bytes).Append(',')
                .Append(chatMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(projectMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((chatMs + projectMs).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(messages.ToString(CultureInfo.InvariantCulture)).AppendLine();
            Console.WriteLine($"{name}: chat {chatMs}ms + project {projectMs}ms = {chatMs + projectMs}ms, {messages} 条消息");
            Console.Out.Flush();
            _ = viewModel;
            try { Directory.Delete(copy, recursive: true); } catch (IOException) { }
        }
        var path = Path.Combine(TempTree.RepositoryRoot, "artifacts", $"user-perceived-open-{Path.GetFileName(TempTree.RepositoryRoot) == "ab-baseline"}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await File.WriteAllTextAsync(path, rows.ToString());
        Console.WriteLine($"CSV: {path}");
    }, TimeSpan.FromMinutes(60));

    private static List<(string Name, string Directory, long Bytes)> TopSessions()
    {
        var all = new List<(string Name, string Directory, long Bytes)>();
        foreach (var root in CorpusRoots())
            foreach (var project in Directory.EnumerateDirectories(root))
                foreach (var session in Directory.EnumerateDirectories(project))
                {
                    var log = Path.Combine(session, LogFileName);
                    if (File.Exists(log))
                        all.Add(($"{Path.GetFileName(project)}/{Path.GetFileName(session)}", session, new FileInfo(log).Length));
                }
        if (Environment.GetEnvironmentVariable("DSH_AB_NAMES") is { Length: > 0 } names)
        {
            var wanted = names.Split(';', StringSplitOptions.RemoveEmptyEntries);
            return [.. all.Where(item => wanted.Any(name => item.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))];
        }
        var count = int.TryParse(Environment.GetEnvironmentVariable("DSH_AB_TOP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 5;
        return [.. all.OrderByDescending(item => item.Bytes).Take(count)];
    }

    private static List<string> CorpusRoots()
    {
        if (Environment.GetEnvironmentVariable("DSH_AB_CORPUS") is { Length: > 0 } overridden)
            return [overridden];
        return [.. new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions"),
        }.Where(Directory.Exists)];
    }

    private static string Stage(GuiTestEnvironment environment, string name, string source)
    {
        var project = name.Split('/')[0];
        var target = Path.Combine(environment.App.Home.Root, "sessions", project, Path.GetFileName(source));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        return target;
    }
}
