using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Agent;

namespace Dsh.Tests;

/**
 * M6 观测接线的自检: 打开会话的汇总打点与每轮请求统计(O1)必须在真实路径上真的写出。
 *
 * 断言读的是 logger 里那一行文本 —— 这两条日志的用途就是事后按 "session open:" / "request prompt:" grep。
 * 请求统计那一条只在真有适配器的请求上写, 所以这里配一个**不可达端点**的 provider:
 * 适配器是真的 openai-compatible, 请求也真的发出去并失败, 不掺任何伪造的模型响应。
 *
 * 临时 home 留在 `artifacts/p1-verify/` 而不是跑完删掉: 文件日志就在那个 home 里, 事后能直接 grep 到原样的一行。
 */
public sealed class TelemetryLogTests
{
    /** 打开路径的打点要落到日志文件里, 级别取默认的 info。 */
    private const string Settings = """
        logging:
          level: info
          file: true
        """;

    /** 端点故意指向本机不可达端口: 请求在日志行之后失败, 不会产生任何伪造的回答。 */
    private const string RequestSettings = """
        logging:
          level: info
          file: true

        providers:
          probe:
            type: openai-compatible
            options:
              baseUrl: http://127.0.0.1:1
              apiKey: telemetry-probe-not-a-real-key
            models: {}
        """;

    [Fact]
    public async Task Resume_WritesSessionOpenSummaryLine()
    {
        var home = Path.Combine(ArtifactsRoot(), $"telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.yaml"), Settings);
        var sessionId = SessionId.Create($"session-{Guid.NewGuid()}");
        var agentOptions = new AgentOptions("test", "test-model");
        using (var app = await ComposeAsync(home))
        {
            var created = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(
                new CreateAgentOptions(sessionId, home, agentOptions), TestContext.Current.CancellationToken);
            created.Agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hello")), new SurfaceOp.Append());
            await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(created.Agent.Session);
        }

        using (var app = await ComposeAsync(home))
        {
            await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Resume(
                new ResumeAgentOptions(sessionId, agentOptions), TestContext.Current.CancellationToken);

            var line = Assert.Single(app.Ctx.Logger.Buffer, message => message.Text.Contains("session open: id=", StringComparison.Ordinal)).Text;
            Assert.StartsWith($"session open: id={sessionId.Value} open_ms=", line);
            Assert.Contains(" firstPaint_ms=-1 interactive_ms=", line);
            Assert.Contains(" stall_ms=-1 events=", line);
            Assert.EndsWith(" windowed=False", line);
        }
    }

    [Fact]
    public async Task Request_WritesPromptStatisticsLine()
    {
        var home = Path.Combine(ArtifactsRoot(), $"telemetry-request-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.yaml"), RequestSettings);
        using var app = await ComposeAsync(home);
        var handle = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(
            new CreateAgentOptions(SessionId.Create($"session-{Guid.NewGuid()}"), home, new AgentOptions("probe", "probe-model")),
            TestContext.Current.CancellationToken);
        try
        {
            handle.Agent.Followup(MessageFactory.CreateUserText("probe"));
            var line = await FirstLineAsync(app, "request prompt:");
            Assert.Contains(" turn=1 step=1 messages=", line);
            Assert.Contains(" chars=", line);
            Assert.Contains(" firstSeq=", line);
            Assert.Contains(" lastSeq=", line);
            Assert.Contains(" checkpointSeq=-1 dropped=0", line);
        }
        finally
        {
            handle.Agent.Cancel(new AgentCancelCause.User());
            await handle.Agent.WhenIdle();
        }
    }

    /** 等到某条日志出现: 请求失败后可能重试, 第一条就是第一次真发请求时写下的那条。 */
    private static async Task<string> FirstLineAsync(HarnessApp app, string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (app.Ctx.Logger.Buffer.FirstOrDefault(message => message.Text.Contains(marker, StringComparison.Ordinal)) is { } found)
                return found.Text;
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
        Assert.Fail($"no log line containing \"{marker}\" appeared");
        return "";
    }

    private static Task<HarnessApp> ComposeAsync(string home)
        => ConfigBoot.Compose(
            new HarnessOptions(HarnessHome.Resolve(home), home),
            Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);

    /** 仓库内的 `artifacts/p1-verify`: 可运行文件的输出只落在项目目录里。 */
    private static string ArtifactsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "artifacts", "p1-verify");
    }
}
