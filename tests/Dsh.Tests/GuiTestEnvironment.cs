using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** GUI 组件测试的最小真实环境: 真实插件组合 + 真实持久化, 不启动 UI。 */
public sealed class GuiTestEnvironment : IDisposable
{
    private readonly string _directory;
    private bool _ownsDirectory = true;

    private GuiTestEnvironment(string directory, HarnessApp app, AgentLoopAgent agent)
    {
        _directory = directory;
        App = app;
        Agent = agent;
    }

    public HarnessApp App { get; }

    public AgentLoopAgent Agent { get; }

    public static async Task<GuiTestEnvironment> CreateAsync()
        => await CreateAsync(HomeSettings);

    public static async Task<GuiTestEnvironment> CreateAsync(string homeSettings)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-gui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var homePath = Path.Combine(directory, "home");
        Directory.CreateDirectory(homePath);
        // 关掉文件日志: 句柄释放时机不受测试控制, 会让临时目录清理偶发失败。
        File.WriteAllText(Path.Combine(homePath, "settings.yaml"), homeSettings);
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory));
        return new GuiTestEnvironment(directory, app, await CreateAgentAsync(app, directory));
    }

    /** 模拟"重启应用": 旧实例关闭, 用同一个 home 重新组合, 并新建一个会话(旧会话不再 live)。 */
    public async Task<GuiTestEnvironment> RestartAsync()
    {
        App.Dispose();
        _ownsDirectory = false;
        var homePath = Path.Combine(_directory, "home");
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), _directory));
        return new GuiTestEnvironment(_directory, app, await CreateAgentAsync(app, _directory));
    }

    /** 关掉文件日志:它的句柄释放时机不受测试控制,会让临时目录清理偶发失败。 */
    public const string HomeSettings = """
        logging:
          file: false
        """;

    private static async Task<AgentLoopAgent> CreateAgentAsync(HarnessApp app, string cwd)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions("test", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return agent;
    }

    public void Dispose()
    {
        App.Dispose();
        if (!_ownsDirectory)
            return;
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }
}
