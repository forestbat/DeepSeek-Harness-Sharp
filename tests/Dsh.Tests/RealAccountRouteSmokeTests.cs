using Dsh.Account;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests;

/**
 * 真实账号登录态的端到端验证(需要本机已登录 DeepSeek 账号, 没有就跳过):
 * 零配置(settings 里一条 provider 都没有), 只靠账号令牌完成"列模型 + 一轮对话"。
 * 前提: 账号插件已启用且 ~/.dsh/web-login 下有授权, 与 GUI 里登录一次等价。
 */
[Trait("Category", "LlmSmoke")]
public sealed class RealAccountRouteSmokeTests
{
    [Fact]
    public async Task ZeroConfig_AccountToken_ListsModelsAndCompletesATurn()
    {
        var source = HarnessHome.Resolve();
        var grant = new AccountStore(source).ReadGrant(AccountConfig.DefaultPlatformOrigin);
        if (grant is null)
            return;
        var home = Path.Combine(Path.GetTempPath(), $"dsh-real-account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "settings.yaml"), $"""
                plugins:
                  "@deepseek-ai/dsh-account":
                    accountEnabled: true
                """);
            var target = HarnessHome.Resolve(home);
            new AccountStore(target).WriteGrant(new AccountGrant(1, grant.Token, AccountConfig.DefaultPlatformOrigin));
            using var app = await HarnessComposer.Compose(
                new HarnessOptions(target, Cwd: Directory.GetCurrentDirectory()),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            var failures = new List<string>();
            app.Ctx.On<AgentErrorNotification>(notification => failures.Add(notification.Error.ToString()),
                new EventOptions { Global = true });
            var handle = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!
                .Create(new CreateAgentOptions(AgentOptions: new AgentOptions()), TestContext.Current.CancellationToken);
            using var detach = handle.Dispose;
            handle.Agent.Followup(MessageFactory.CreateUserText("只回复两个字:收到"));
            await handle.Agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

            var events = handle.Agent.Session.SnapshotEvents();
            Assert.Empty(failures);
            var config = handle.Agent.Session.RequestHeader()?.Config;
            Assert.Equal("deepseek-account", config?.Provider);
            var reply = string.Concat(events
                .Select(sessionEvent => sessionEvent.Data)
                .OfType<AssistantMessagePayload>()
                .SelectMany(payload => payload.Message.Content)
                .OfType<TextBlock>()
                .Select(block => block.Text));
            Assert.Contains("收到", reply);
        }
        finally
        {
            try
            {
                Directory.Delete(home, true);
            }
            catch (IOException)
            {
            }
        }
    }
}