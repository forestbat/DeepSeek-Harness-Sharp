using System.Text;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests;

/**
 * TOON 真实 LLM 实验: 用 .experiment/toon-home(deepseek-official/deepseek-flash + dsh-toon 开,input 白名单 bash)
 * 起一个真实 agent,让它跑 bash,检验模型是否会按提示词生成 ```toon 围栏调用(输出侧)以及读得懂 TOON 工具结果(输入侧)。
 * 运行: Dsh.Tests -class "Dsh.Tests.ToonLlmExperimentTests"
 */
[Trait("Category", "LlmSmoke")]
public sealed class ToonLlmExperimentTests
{
    private const string HomePath = ".experiment/toon-home";

    [Fact]
    public async Task ToonExperiment_RealLlmCallsBash()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var home = Path.Combine(root, HomePath);
        Assert.True(File.Exists(Path.Combine(home, "settings.yaml")), $"实验 home 不存在: {home}");
        _ = typeof(Dsh.Toon.Plugin).Assembly.FullName;

        var options = new HarnessOptions(
            HarnessHome.Resolve(home),
            Cwd: root,
            Provider: "deepseek-official",
            Model: "deepseek-flash",
            ReasoningEffort: "high");
        using var app = await HarnessComposer.Compose(options);
        await Console.Error.WriteLineAsync(
            $"activations: {string.Join(", ", app.Composition!.Activations.Select(a => a.Name))}");
        var failures = new List<string>();
        app.Ctx.On<AgentErrorNotification>(notification => failures.Add(notification.Error.ToString()), new EventOptions { Global = true });

        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"toon-exp-{Guid.NewGuid():N}"),
            root,
            new AgentOptions("deepseek-official", "deepseek-flash")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        var prompts = new[]
        {
            "用 bash 运行 ls,然后用一句话告诉我当前目录下有哪些 .slnx 文件。",
            "再用 bash 看一次当前目录,确认 .slnx 文件的数量。",
            "用 bash 统计当前目录下按扩展名分组的文件数量。",
            "把你这几轮做了哪些工具调用总结成一句话。",
        };
        foreach (var prompt in prompts)
        {
            agent.Followup(MessageFactory.CreateUserText(prompt));
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
        }

        var events = agent.Session.SnapshotEvents();
        var transcript = RenderTranscript(events, failures);
        var reportPath = Path.Combine(root, "plans", "TOON实验记录-2026-10-02.md");
        await File.WriteAllTextAsync(reportPath, transcript, TestContext.Current.CancellationToken);

        var toolCalls = events.Select(sessionEvent => sessionEvent.Data).OfType<ToolCallPayload>().ToList();
        Assert.True(toolCalls.Count > 0,
            $"模型没有产生任何工具调用;transcript: {reportPath}; failures: {string.Join(" | ", failures)}");
        Assert.True(failures.Count == 0,
            $"多轮出现失败;transcript: {reportPath}; failures: {string.Join(" | ", failures)}");
    }

    private static string DescribeChunk(StreamChunk chunk) => chunk switch
    {
        StreamChunk.BlockStart start => $"index={start.Index} type={start.BlockType}",
        StreamChunk.BlockEnd end => $"index={end.Index} block={end.Block.Type}",
        StreamChunk.TextDelta text => $"index={text.Index} len={text.Text.Length}",
        StreamChunk.ReasoningDelta reasoning => $"index={reasoning.Index} len={reasoning.Text.Length}",
        StreamChunk.ToolCallDelta call => $"index={call.Index} id={call.Id} name={call.Name}",
        StreamChunk.Finish finish => $"reason={finish.Reason.Kind}",
        _ => "",
    };

    private static string RenderTranscript(IReadOnlyList<SessionEvent> events, List<string> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TOON 真实 LLM 实验记录(2026-10-02)");
        builder.AppendLine();
        builder.AppendLine("模型: deepseek-official/deepseek-flash;插件: @deepseek-ai/dsh-toon(output 开,input 白名单 bash)。");
        builder.AppendLine();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case AssistantMessagePayload payload:
                    builder.AppendLine("## Assistant");
                    foreach (var block in payload.Message.Content)
                        builder.AppendLine(block switch
                        {
                            ToolCallBlock call => $"[tool-call] {call.Name} {call.Arguments}",
                            ReasoningBlock reasoning => $"[reasoning] {reasoning.Text}",
                            _ => $"[text] {(block as TextBlock)?.Text}",
                        });
                    builder.AppendLine();
                    break;
                case ToolCallPayload payload:
                    builder.AppendLine($"## ToolCall turn={payload.Turn} step={payload.Step} {payload.Name}");
                    builder.AppendLine("```");
                    builder.AppendLine(payload.Arguments);
                    builder.AppendLine("```");
                    break;
                case ToolResultPayload payload:
                    builder.AppendLine($"## ToolResult call={payload.Message.Block.ToolCallId} error={payload.Error?.Code ?? "none"}");
                    builder.AppendLine("```");
                    builder.AppendLine(string.Join("\n", payload.Message.Block.Content.OfType<TextBlock>().Select(block => block.Text)));
                    builder.AppendLine("```");
                    break;
                case AssistantChunkPayload payload:
                    builder.AppendLine($"[chunk] {payload.Chunk.GetType().Name} {DescribeChunk(payload.Chunk)}");
                    break;
            }
        }
        if (failures.Count > 0)
            builder.AppendLine($"## Failures\n{string.Join("\n", failures)}");
        return builder.ToString();
    }
}
