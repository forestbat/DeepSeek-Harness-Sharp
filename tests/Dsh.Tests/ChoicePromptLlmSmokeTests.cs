using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * Opt-in, real model -> ask_user_question -> default daemon/proxy TUI -> model consumers.
 * Run with a project-local QA DSH_HOME template configured with an authorized model, real provider credentials, persistence and tool-ask-user enabled.
 * CopyRealConfiguration supplies each terminal's private storage; no endpoint, credential or product test switch lives here.
 * The requested question data is fixed, but the assistant's acknowledgement is never graded.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class ChoicePromptLlmSmokeTests
{
    private const string Draft = "choice-draft-tail";
    private const string RestoredDraft = "choice-draft-Xtail";
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";
    private const string Left = "\u001b[D";
    private const string Down = "\u001b[B";
    private const string Up = "\u001b[A";
    private const string Home = "\u001b[H";
    private const string End = "\u001b[F";

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealModel_DefaultProxy_HoverWrappedCjkAndKeyboardSubmitSingleChoice()
    {
        const string questions = """
            {"questions":[{"id":"route","header":"CHOICE_SINGLE","question":"Choose a route.","allowCustom":false,"multi_select":false,"options":[
              {"label":"ALPHA 初始","value":"alpha","default":true},
              {"label":"DISABLED 不可选","value":"disabled","disabled":true},
              {"label":"GAMMA 目标","value":"gamma","description":"长选项的中文说明需要在真实终端换行并保持命中区域正确。长选项的中文说明需要在真实终端换行并保持命中区域正确。长选项的中文说明需要在真实终端换行并保持命中区域正确。 HOVER_TAIL"}
            ]}]}
            """;
        await using var tui = await RealTui.StartAsync(questions, "CHOICE_SINGLE");
        await tui.WaitForScreenAsync(screen => Checked(screen, 1) && Focused(screen, "ALPHA")
            && Styled(screen, "DISABLED", CellStyle.Dim) && Locate(screen, "HOVER_TAIL") is not null,
            "The default, disabled and wrapped CJK choices were not rendered.");
        var screen = tui.Screen;
        var firstLine = Locate(screen, "GAMMA")!.Value;
        var continuation = Locate(screen, "HOVER_TAIL")!.Value;
        Assert.True(continuation.Y > firstLine.Y, "The CJK option must actually wrap before testing its continuation hit area.");
        Assert.False(Focused(screen, "HOVER_TAIL"));

        // SGR 35 means motion with NO button, not a drag or a synthetic control callback.
        await tui.WriteAsync($"\u001b[<35;{continuation.X + 1};{continuation.Y + 1}M");
        await tui.WaitForScreenAsync(current => Focused(current, "HOVER_TAIL") && !Focused(current, "ALPHA")
            && Checked(current, 1) && !Checked(current, 3),
            "No-button SGR hover did not move focus without changing the selected answer.");
        await tui.WriteAsync(Up);
        await tui.WaitForScreenAsync(current => Focused(current, "ALPHA") && !Focused(current, "DISABLED"),
            "Up did not skip the disabled choice.");
        await tui.WriteAsync($"{Down} ");
        await tui.WaitForScreenAsync(current => Focused(current, "GAMMA") && Checked(current, 3) && !Checked(current, 1),
            "Down/Space did not replace the single selection with GAMMA.");
        await tui.WriteAsync("\r");
        var result = await tui.WaitForCompletedResultAsync();
        AssertAnswered(result);
        var answer = Assert.Single(result.GetProperty("answers").EnumerateArray());
        Assert.Equal("route", answer.GetProperty("id").GetString());
        Assert.Equal(new[] { "gamma" }, Selected(answer));
        Assert.False(answer.TryGetProperty("custom", out _));
        await tui.AssertDraftRestoredAndExitAsync();
    }

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealModel_DefaultProxy_MultiQuestionMultiSelectAndCjkCustomSubmit()
    {
        const string questions = """
            {"questions":[
              {"id":"colours","header":"CHOICE_MULTI","question":"Choose colours.","multi_select":true,"allowCustom":false,"options":[
                {"label":"RED 红","value":"red"},{"label":"GREEN 绿","value":"green"},{"label":"BLUE 蓝","value":"blue"}]},
              {"id":"custom","header":"CHOICE_CUSTOM","question":"Enter your own CJK answer.","multi_select":false,"allowCustom":true,"options":[
                {"label":"PRESET 预设","value":"preset"}]}
            ]}
            """;
        await using var tui = await RealTui.StartAsync(questions, "CHOICE_MULTI");
        await tui.WriteAsync($"{Home} {End} ");
        await tui.WaitForScreenAsync(screen => Checked(screen, 1) && !Checked(screen, 2) && Checked(screen, 3),
            "Home/End/Space did not preserve both multi-select choices.");
        await tui.WriteAsync("\r");
        await tui.WaitForScreenAsync(screen => Visible(screen, "CHOICE_CUSTOM") && Visible(screen, "Custom:"),
            "Submitting the first answer did not advance to the second question.");
        tui.AssertNoResultYet();
        await tui.WriteAsync($"\t自定义甲乙{Left}\u007f林");
        await tui.WaitForScreenAsync(screen => Visible(screen, "Custom: 自定义林乙") && Focused(screen, "Custom:"),
            "The custom field did not render the edited CJK answer.");
        // Tab leaves custom input for Submit; Enter must not send a new chat message.
        await tui.WriteAsync("\t\r");
        var result = await tui.WaitForCompletedResultAsync();
        AssertAnswered(result);
        var answers = result.GetProperty("answers").EnumerateArray().ToArray();
        Assert.Equal(2, answers.Length);
        Assert.Equal("colours", answers[0].GetProperty("id").GetString());
        Assert.Equal(new[] { "red", "blue" }, Selected(answers[0]));
        Assert.False(answers[0].TryGetProperty("custom", out _));
        Assert.Equal("custom", answers[1].GetProperty("id").GetString());
        Assert.Empty(Selected(answers[1]));
        Assert.Equal("自定义林乙", answers[1].GetProperty("custom").GetString());
        await tui.AssertDraftRestoredAndExitAsync();
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealModel_DefaultProxy_CancelDiscardsWholeQuestionGroupAndContinuesModel(bool clickCancel)
    {
        const string questions = """
            {"questions":[
              {"id":"partial","header":"CHOICE_PARTIAL","question":"Choose the first answer.","multi_select":false,"allowCustom":false,"options":[
                {"label":"FIRST 一","value":"first"},{"label":"SECOND 二","value":"second"}]},
              {"id":"discard","header":"CHOICE_CANCEL","question":"Cancel this entire group.","multi_select":false,"allowCustom":true,"options":[
                {"label":"UNUSED 未用","value":"unused"}]}
            ]}
            """;
        await using var tui = await RealTui.StartAsync(questions, "CHOICE_PARTIAL");
        await tui.WriteAsync("2");
        await tui.WaitForScreenAsync(screen => Checked(screen, 2), "Numeric selection did not select the first question's SECOND answer.");
        await tui.WriteAsync("\r");
        await tui.WaitForScreenAsync(screen => Visible(screen, "CHOICE_CANCEL"), "The second question did not open.");
        tui.AssertNoResultYet();
        await tui.WriteAsync("\t应被丢弃");
        await tui.WaitForScreenAsync(screen => Visible(screen, "Custom: 应被丢弃"), "The pending custom answer did not render.");
        if (clickCancel)
        {
            var cancel = Locate(tui.Screen, "[ Cancel ]")!.Value;
            await tui.WriteAsync($"\u001b[<0;{cancel.X + 2};{cancel.Y + 1}M\u001b[<0;{cancel.X + 2};{cancel.Y + 1}m");
        }
        else
        {
            await tui.WriteAsync("\u001b");
        }
        var result = await tui.WaitForCompletedResultAsync();
        Assert.Equal("dismissed", result.GetProperty("status").GetString());
        Assert.Equal("user_cancelled", result.GetProperty("reason").GetString());
        Assert.Empty(result.GetProperty("answers").EnumerateArray());
        await tui.AssertDraftRestoredAndExitAsync();
    }

    private static void AssertAnswered(JsonElement result)
    {
        Assert.Equal("answered", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reason").ValueKind);
    }

    private static string[] Selected(JsonElement answer)
        => answer.GetProperty("selected").EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static (int X, int Y)? Locate(VtScreen screen, string ascii)
    {
        for (var y = 0; y < screen.Height; y++)
        {
            var row = screen.Row(y);
            for (var x = 0; x <= row.Length - ascii.Length; x++)
            {
                var matches = true;
                for (var i = 0; i < ascii.Length; i++)
                    if (row[x + i].Character != ascii[i]) { matches = false; break; }
                if (matches) return (x, y);
            }
        }
        return null;
    }

    private static bool Styled(VtScreen screen, string ascii, CellStyle style)
        => Locate(screen, ascii) is { } point && (screen.Row(point.Y)[point.X].Style & style) != 0;

    private static bool Focused(VtScreen screen, string ascii) => Styled(screen, ascii, CellStyle.Reverse);

    private static bool Checked(VtScreen screen, int number)
        => Locate(screen, $"{number}. [") is { } point && screen.Row(point.Y)[point.X + 4].Character == 'x';

    private static string RowText(VtScreen screen, int y)
    {
        var text = new StringBuilder(screen.Width);
        foreach (var cell in screen.Row(y))
            if (cell.Character != '\0') text.Append(cell.Character);
        return text.ToString().TrimEnd();
    }

    private static bool Visible(VtScreen screen, string text)
    {
        for (var y = 0; y < screen.Height; y++)
            if (RowText(screen, y).Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Input(VtScreen screen)
    {
        for (var y = screen.Height - 1; y >= 0; y--)
        {
            var text = RowText(screen, y).Trim();
            if (text.StartsWith(">", StringComparison.Ordinal)) return text;
        }
        return "";
    }

    private sealed class RealTui(PtyTuiHarness terminal, string provider, string model) : IAsyncDisposable
    {
        private readonly JsonlSessionPersistence _persistence = new(new HarnessHome(terminal.Home).SessionsPath);
        private ToolCallPayload? _call;
        private bool _exited;

        public VtScreen Screen => terminal.ReadTerminalScreen();
        public Task WriteAsync(string text) => terminal.WriteTextAsync(text);

        public static async Task<RealTui> StartAsync(string arguments, string heading)
        {
            // Fail before CopyRealConfiguration can read a personal home, or a model request can escape.
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var template = HarnessHome.Default();
            var qa = Environment.GetEnvironmentVariable(HarnessHome.HomeEnv);
            Assert.True(!string.IsNullOrWhiteSpace(qa), "Set DSH_HOME to an isolated project-local QA template before explicitly running this smoke.");
            var relative = Path.GetRelativePath(root, template.Root);
            Assert.True(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
                "The QA configuration template must be inside this worktree, never the personal home.");
            Assert.Equal(Path.GetFullPath(qa!), template.Root);
            Assert.Equal(template.Root, PtyDaemonPaths.DefaultRoot());
            Assert.True(File.Exists(Path.Combine(template.Root, "settings.yaml")), "The isolated QA template requires real settings.yaml.");
            var settings = HarnessSettings.Load(template);
            var route = settings.ResolveDefaultModel()
                ?? throw new InvalidOperationException("The QA template requires a default provider/model.");
            Assert.True(settings.Providers.ContainsKey(route.Provider), "The QA template requires its real default provider.");
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DSH_PTY_CHILD")), "Run the smoke outside a resident TUI child so the default proxy path is exercised.");

            var tui = new RealTui(await PtyTuiHarness.StartAsync(), route.Provider, route.Model);
            try
            {
                await tui.WaitForScreenAsync(screen => screen.IsAlternateScreen && Input(screen) == ">",
                    "The real default dsharp TUI did not reach its empty input.", TimeSpan.FromSeconds(150));
                var resident = Assert.Single(await PtyDaemonClient.ListAsync(TestContext.Current.CancellationToken), tui.Owns);
                Assert.True(resident.WantsMouse, "The real default TUI must be a mouse-enabled daemon session, not a direct TUI.");
                Assert.Equal("Running", resident.Status);
                Assert.True(tui.Screen.SgrMouse, "The proxy did not enable real SGR mouse reporting.");
                var instruction = $$"""
                    Call the actual ask_user_question tool exactly once with the following question data.
                    Preserve the supplied ids, headings, option labels, values, flags and descriptions.
                    Pass questions as a JSON array, not a JSON-encoded string.
                    Use only this tool, then acknowledge its actual result briefly and finish, including when dismissed.
                    Arguments: {{arguments}}
                    """;
                // One terminal write queues the unsent draft and middle cursor immediately after Submit and before the network answer.
                await tui.WriteAsync($"{PasteStart}{instruction}{PasteEnd}\r{Draft}{Left}{Left}{Left}{Left}");
                await tui.WaitAsync(() => tui.Events().Any(entry => entry.Data is ToolCallPayload),
                    TimeSpan.FromSeconds(120), "The real model's tool call was not persisted.");
                var call = tui.Events().Select(entry => entry.Data).OfType<ToolCallPayload>().Last();
                Assert.Equal("ask_user_question", call.Name);
                tui._call = call;
                AssertQuestionData(arguments, call.Arguments);
                await tui.WaitForScreenAsync(screen => Visible(screen, "Choice: arrows/Home/End") && Visible(screen, heading),
                    "The persisted question data did not appear in the real TUI.");
                Assert.False(Visible(tui.Screen, Draft), "The stashed chat draft leaked into the active question prompt.");
                return tui;
            }
            catch
            {
                await tui.DisposeAsync();
                throw;
            }
        }

        // These are user-supplied tool data assertions, not assertions on generated assistant prose.
        private static void AssertQuestionData(string requested, string actual)
        {
            using var expectedDocument = JsonDocument.Parse(requested);
            using var actualDocument = JsonDocument.Parse(actual);
            var expected = expectedDocument.RootElement.GetProperty("questions").EnumerateArray().ToArray();
            var received = actualDocument.RootElement.GetProperty("questions").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, received.Length);
            for (var q = 0; q < expected.Length; q++)
            {
                foreach (var name in new[] { "id", "header" })
                    Assert.Equal(expected[q].GetProperty(name).GetString(), received[q].GetProperty(name).GetString());
                foreach (var name in new[] { "multi_select", "allowCustom" })
                    Assert.Equal(expected[q].GetProperty(name).GetBoolean(), Boolean(received[q], name, name == "allowCustom"));
                var options = expected[q].GetProperty("options").EnumerateArray().ToArray();
                var actualOptions = received[q].GetProperty("options").EnumerateArray().ToArray();
                Assert.Equal(options.Length, actualOptions.Length);
                for (var i = 0; i < options.Length; i++)
                {
                    foreach (var name in new[] { "label", "value", "description" })
                        if (options[i].TryGetProperty(name, out var value))
                            Assert.Equal(value.GetString(), actualOptions[i].GetProperty(name).GetString());
                    foreach (var name in new[] { "disabled", "default" })
                        Assert.Equal(Boolean(options[i], name), Boolean(actualOptions[i], name));
                }
            }
        }

        private static bool Boolean(JsonElement value, string name, bool fallback = false)
            => value.TryGetProperty(name, out var property) ? property.GetBoolean() : fallback;

        private IReadOnlyList<SessionEvent> Events()
        {
            var sessions = _persistence.List();
            if (sessions.Count == 0) return [];
            using var handle = _persistence.Open(Assert.Single(sessions).Header.Id, SessionAccess.Read);
            return handle.Read();
        }

        public void AssertNoResultYet()
            => Assert.DoesNotContain(Events(), entry => entry.Data is ToolResultPayload result && result.Message.Block.ToolCallId == _call!.CallId);

        public async Task<JsonElement> WaitForCompletedResultAsync()
        {
            await WaitAsync(() => Events().Any(entry => entry.Data is TurnEndPayload), TimeSpan.FromSeconds(120),
                "The real model did not complete after receiving the question result (Esc must not cancel the turn).");
            var events = Events();
            Assert.NotNull(_call);
            var call = Assert.Single(events.Select(entry => entry.Data).OfType<ToolCallPayload>(),
                item => item.CallId == _call.CallId);
            var result = Assert.Single(events.Select(entry => entry.Data).OfType<ToolResultPayload>(),
                item => item.Message.Block.ToolCallId == call.CallId);
            Assert.Equal(call.CallId, result.Message.Block.ToolCallId);
            Assert.Null(result.Error);
            Assert.False(result.Message.Block.IsError == true);
            var end = Assert.Single(events.Select(entry => entry.Data).OfType<TurnEndPayload>());
            Assert.Equal(call.Turn, end.Turn);
            Assert.IsType<TurnEndReason.Completed>(end.Reason);
            var resultIndex = events.ToList().FindIndex(entry => ReferenceEquals(entry.Data, result));
            // A real model message after the result, in a later inference step, proves continuation.
            // Its natural-language wording is deliberately not compared with an expected phrase.
            Assert.Contains(events.Skip(resultIndex + 1), entry => entry.Data is AssistantMessagePayload assistant
                && assistant.Turn == call.Turn && assistant.Step > call.Step && !assistant.Interrupted
                && assistant.Message.ModelSource.Provider == provider && assistant.Message.ModelSource.Model == model);
            Assert.Single(events.Select(entry => entry.Data).OfType<UserMessagePayload>(),
                message => message.Message.Source is UserMessageSource);
            var text = Assert.Single(result.Message.Block.Content.OfType<TextBlock>());
            using var json = JsonDocument.Parse(text.Text);
            return json.RootElement.Clone();
        }

        public async Task AssertDraftRestoredAndExitAsync()
        {
            await WaitForScreenAsync(screen => !Visible(screen, "Choice: arrows/Home/End") && Input(screen) == $"> {Draft}",
                "The original unsent draft was not restored after closing the entire question group.");
            await WriteAsync("X");
            await WaitForScreenAsync(screen => Input(screen) == $"> {RestoredDraft}",
                "The original middle-of-draft cursor was not restored.");
            await WriteAsync($"{End}{new string('\u007f', RestoredDraft.Length)}");
            await WaitForScreenAsync(screen => Input(screen) == ">", "Could not clear the unsent draft before normal exit.");
            await WriteAsync("/exit\r");
            await WaitAsync(() => terminal.Session.Status != PtySessionStatus.Running, TimeSpan.FromSeconds(20),
                "Normal /exit left the default proxy running.");
            Assert.Equal(0, terminal.Session.ExitCode);
            await WaitAsync(() => !terminal.ReadTerminalScreen().IsAlternateScreen, TimeSpan.FromSeconds(10),
                "Normal exit did not restore the outer PTY's main screen.");
            var owned = (await PtyDaemonClient.ListAsync(TestContext.Current.CancellationToken)).Where(Owns);
            Assert.DoesNotContain(owned, session => session.Status == "Running");
            _exited = true;
        }

        public Task WaitForScreenAsync(Func<VtScreen, bool> predicate, string failure, TimeSpan? timeout = null)
            => WaitAsync(() => predicate(Screen), timeout ?? TimeSpan.FromSeconds(15), failure);

        private async Task WaitAsync(Func<bool> predicate, TimeSpan timeout, string failure)
        {
            var start = Stopwatch.GetTimestamp();
            do
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (predicate()) return;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            } while (Stopwatch.GetElapsedTime(start) < timeout);
            Assert.Fail(failure); // Do not dump private configuration, model traffic or raw terminal output.
        }

        private bool Owns(PtyDaemonSessionDto session)
            => string.Equals(session.Home, terminal.Home, StringComparison.OrdinalIgnoreCase);

        public async ValueTask DisposeAsync()
        {
            terminal.SaveTranscript();
            _persistence.Dispose();
            try
            {
                // Never kill the shared QA daemon or another terminal; match the structured Home field.
                // Use an independent budget so failed/cancelled tests still reap their resident TUI.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var sessions = (await PtyDaemonClient.ListAsync(cleanup.Token)).Where(Owns).ToArray();
                foreach (var session in sessions.Where(session => session.Status == "Running"))
                    await PtyDaemonClient.KillAsync(session.Id, cleanup.Token);
                var remaining = (await PtyDaemonClient.ListAsync(cleanup.Token)).Where(Owns);
                Assert.DoesNotContain(remaining, session => session.Status == "Running");
                if (_exited) Assert.NotEqual(PtySessionStatus.Running, terminal.Session.Status);
            }
            finally
            {
                terminal.Dispose();
                Assert.False(Directory.Exists(terminal.Home), "The private terminal home was not removed during cleanup.");
            }
        }
    }
}
