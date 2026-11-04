using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Ptc;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Tools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

/** Opt-in real-provider consumers of batched image content and user-first plugin development. */
[Trait("Category", "LlmSmoke")]
[Collection(SerialProcessCollection.Name)]
public sealed class CapabilityGuidanceLlmSmokeTests
{
    private const int ImageSize = 128;
    private const string PtcPackage = "@deepseek-ai/dsh-ptc";
    private const string PtcMessagePlugin = "ptc";
    private static readonly TimeSpan ModelBudget = TimeSpan.FromSeconds(120);
    private static readonly HashSet<string> ProcessTools =
        ["bash", "pwsh", "terminal_open", "terminal_send", "terminal_close", "run_csx", "run_csharp_repl"];

    [Fact(Explicit = true)]
    public async Task RealModel_OnePtcBatchDeliversBothImagesToTheVisionModel()
    {
        using var fixture = await ModelFixture.CreateAsync(debugBatching: true);
        var first = Path.Combine(fixture.Home, "image-a.png");
        var second = Path.Combine(fixture.Home, "image-b.png");
        await SaveQuadrantsAsync(first, [Color.Red, Color.Blue, Color.Green, Color.Yellow]);
        await SaveQuadrantsAsync(second, [Color.Purple, Color.Black, Color.White, Color.Orange]);
        var markdown = Path.Combine(fixture.Home, "images.md");
        await File.WriteAllTextAsync(markdown, "![first](image-a.png)\n![second](image-b.png)\n", TestContext.Current.CancellationToken);
        fixture.Agent.Followup(MessageFactory.CreateUserText(
            $"""
            Read {markdown} and resolve both linked image paths.
            Use the generated C# tool SDK inside exactly one run_code call for both read_image operations, not separate top-level read_image calls.
            Inspect the delivered image content and list each image's four quadrant colours in English, top-left to bottom-right; do not guess from file names.
            """));
        await fixture.Agent.WhenIdle().WaitAsync(ModelBudget, TestContext.Current.CancellationToken);
        var history = fixture.Agent.Session.SnapshotEvents().Select(entry => entry.Data).ToArray();
        Assert.IsType<TurnEndReason.Completed>(history.OfType<TurnEndPayload>().Last().Reason);
        var program = Assert.Single(history.OfType<ToolCallPayload>(), call => call.Name == ToolRunCode.ToolName);
        var programResult = Assert.Single(history.OfType<ToolResultPayload>(),
            result => result.Message.Block.ToolCallId == program.CallId);
        Assert.False(programResult.Message.Block.IsError == true, MessageText.Flatten(programResult.Message.Block.Content));
        var reads = history.OfType<PtcDispatchPayload>().Where(entry => entry.ToolName == ReadImageTool.ToolName && !entry.IsError).ToArray();
        Assert.Single(reads.Select(entry => entry.RunId).Distinct(StringComparer.Ordinal));
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal),
            reads.Select(entry => entry.Result!.Value.GetProperty("path").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(2, history.OfType<UserMessagePayload>()
            .Where(entry => entry.Message.Source is PluginMessageSource { Plugin: PtcMessagePlugin })
            .SelectMany(entry => entry.Message.Content).OfType<ImageBlock>().Count());
        var reply = string.Join("\n", history.OfType<AssistantMessagePayload>().Select(entry => MessageText.Flatten(entry.Message.Content)));
        foreach (var colour in new[] { "red", "blue", "green", "yellow", "purple", "black", "white", "orange" })
            Assert.True(reply.Contains(colour, StringComparison.OrdinalIgnoreCase), $"The real model did not identify {colour} from the image content.");
    }

    [Fact(Explicit = true)]
    public async Task RealModel_PluginDevelopmentWaitsForProcessDecision_AndDismissalDoesNotExecute()
    {
        using var fixture = await ModelFixture.CreateAsync(debugBatching: false);
        var pending = new TaskCompletionSource<AskUserQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = fixture.App.Ctx.OnWaterfall<UserQuestionsRequestNotification>((notification, _) =>
        {
            pending.TrySetResult(notification.Request);
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });
        try
        {
            fixture.Agent.Followup(MessageFactory.CreateUserText(
                """
                Before validating the preinstalled Dsh.Inspection plugin, ask me to choose how its validation processes should be handled.
                Use the actual ask_user_question tool and offer an isolated new instance, restart of an explicitly owned instance, and task-scoped automatic handling.
                This turn is only for that question; do not inspect files, run execution tools or perform validation.
                """));
            await Task.WhenAny(pending.Task, fixture.Agent.WhenIdle()).WaitAsync(ModelBudget, TestContext.Current.CancellationToken);
            Assert.True(pending.Task.IsCompletedSuccessfully, "The real model ended its turn without requesting the actual process decision.");
            Assert.DoesNotContain(fixture.Agent.Session.SnapshotEvents().Select(entry => entry.Data),
                data => data is ToolCallPayload call && ProcessTools.Contains(call.Name)
                    || data is PtcDispatchStartPayload dispatch && ProcessTools.Contains(dispatch.ToolName));
            answer.TrySetResult(AskUserQuestionAnswer.Dismissed);
            await fixture.Agent.WhenIdle().WaitAsync(ModelBudget, TestContext.Current.CancellationToken);
            var history = fixture.Agent.Session.SnapshotEvents().Select(entry => entry.Data).ToArray();
            Assert.DoesNotContain(history, data => data is ToolCallPayload call && ProcessTools.Contains(call.Name)
                || data is PtcDispatchStartPayload dispatch && ProcessTools.Contains(dispatch.ToolName));
            Assert.IsType<TurnEndReason.Completed>(history.OfType<TurnEndPayload>().Last().Reason);
        }
        finally
        {
            answer.TrySetResult(AskUserQuestionAnswer.Dismissed);
            remove();
        }
    }

    private static async Task SaveQuadrantsAsync(string path, Color[] colours)
    {
        var pixels = colours.Select(colour => colour.ToPixel<Rgba32>()).ToArray();
        using var image = new Image<Rgba32>(Configuration.Default, ImageSize, ImageSize);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = pixels[(y < ImageSize / 2 ? 0 : 2) + (x < ImageSize / 2 ? 0 : 1)];
            }
        });
        await image.SaveAsPngAsync(path, TestContext.Current.CancellationToken);
    }

    private sealed class ModelFixture(HarnessApp app, AgentLoopAgent agent, string home, IDisposable attachment) : IDisposable
    {
        public HarnessApp App { get; } = app;
        public AgentLoopAgent Agent { get; } = agent;
        public string Home { get; } = home;

        public static async Task<ModelFixture> CreateAsync(bool debugBatching)
        {
            var root = RealHostProcesses.CreateHome("c");
            HarnessApp? app = null;
            try
            {
                RealHostProcesses.CopyRealConfiguration(root);
                var home = new HarnessHome(root);
                var settings = HarnessSettings.Load(home);
                var (provider, model) = settings.ResolveDefaultModel() ?? throw new InvalidOperationException("The real model smoke requires a configured default model.");
                settings.Plugins[PtcPackage] = new PluginSetting { Parameters = new() { ["debug_batching"] = debugBatching } };
                settings.SavePlugins(home);
                app = await ConfigBoot.Compose(new HarnessOptions(home, root, Provider: provider, Model: model, IsTui: false));
                var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                var handle = await agents.Create(new CreateAgentOptions(SessionId.Create($"capability-{Guid.NewGuid():N}"), root,
                    new AgentOptions(provider, model)), TestContext.Current.CancellationToken);
                return new ModelFixture(app, (AgentLoopAgent)handle.Agent, root, handle.Dispose);
            }
            catch
            {
                app?.Dispose();
                TempTree.Delete(root);
                throw;
            }
        }

        public void Dispose()
        {
            attachment.Dispose();
            App.Dispose();
            TempTree.Delete(Home);
        }
    }
}
