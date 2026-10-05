using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

/**
 * 真实 LLM 图片端到端: 把一张纯红 PNG 作为 ImageBlock 发给真实模型, 断言回复里出现"红"。
 * 需要本机 ~/.dsh/settings.yaml 配好可用 provider(无 key 则跳过), 用 DSH_HOME 可指向别处。
 */
[Trait("Category", "LlmSmoke")]
public sealed class RealLlmImageSmokeTests
{
    [Fact]
    public async Task RealLlm_AgentDescribesPastedImage()
    {
        var settings = HarnessSettings.Load(HarnessHome.Resolve());
        var providerId = Environment.GetEnvironmentVariable("DSH_REAL_LLM_PROVIDER") ?? "deepseek-official";
        var modelId = Environment.GetEnvironmentVariable("DSH_REAL_LLM_MODEL") ?? "deepseek-v4-flash";
        if (!settings.Providers.TryGetValue(providerId, out var provider)
            || string.IsNullOrEmpty(provider.Options?.ApiKey))
            return;

        var home = Path.Combine(Path.GetTempPath(), $"dsh-real-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            File.Copy(Path.Combine(HarnessHome.Resolve().Root, "settings.yaml"), Path.Combine(home, "settings.yaml"), overwrite: true);
            var options = new HarnessOptions(
                HarnessHome.Resolve(home),
                Cwd: Directory.GetCurrentDirectory(),
                Provider: providerId,
                Model: modelId);
            using var app = await HarnessComposer.Compose(options);
            var store = app.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName)
                ?? throw new InvalidOperationException("attachment store is not registered");
            var png = QuadrantPng(128);
            var reference = store.Put(png, "image/png", 128, 128, "quadrants.png");

            var failures = new List<string>();
            app.Ctx.On<AgentErrorNotification>(notification => failures.Add(notification.Error.ToString()), new EventOptions { Global = true });
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create($"img-{Guid.NewGuid():N}"),
                Directory.GetCurrentDirectory(),
                new AgentOptions(providerId, modelId)), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserMessage(
                [new TextBlock("这张图的四个象限从左到右、从上到下依次是什么颜色? 只按顺序列出四个颜色词。"), new ImageBlock(reference)]));
            await agent.WhenIdle().WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

            var text = string.Join(
                " ",
                agent.Session.SnapshotEvents()
                    .Select(sessionEvent => sessionEvent.Data)
                    .OfType<AssistantMessagePayload>()
                    .SelectMany(payload => payload.Message.Content)
                    .OfType<TextBlock>()
                    .Select(block => block.Text));
            Console.WriteLine($"[image-smoke] reply: {text}");
            Assert.True(text.Length > 0, $"no assistant text; failures: {string.Join(" | ", failures)}");
            // 盲模型无法同时按序猜中四个不同颜色, 因此这组断言能区分"真的看到了"与"瞎猜"。
            Assert.Contains("红", text);
            Assert.Contains("蓝", text);
            Assert.Contains("绿", text);
            Assert.Contains("黄", text);
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

    /** 左上红 / 右上蓝 / 左下绿 / 右下黄, 用于验证模型真正读到了像素。 */
    private static byte[] QuadrantPng(int size)
    {
        using var image = new Image<Rgba32>(Configuration.Default, size, size);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var top = y < accessor.Height / 2;
                    var left = x < row.Length / 2;
                    row[x] = (top, left) switch
                    {
                        (true, true) => new Rgba32(255, 0, 0),
                        (true, false) => new Rgba32(0, 0, 255),
                        (false, true) => new Rgba32(0, 255, 0),
                        _ => new Rgba32(255, 255, 0),
                    };
                }
            }
        });
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
