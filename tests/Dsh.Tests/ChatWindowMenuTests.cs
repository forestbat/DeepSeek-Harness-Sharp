using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tui;
using System.Text.RegularExpressions;

namespace Dsh.Tests;

public class ChatWindowMenuTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    private AgentLoopAgent? _lastAgent;

    [Fact]
    public async Task Slash_Opens_Command_Popup_With_All_Commands()
    {
        using var chat = await CreateChat();

        Type(chat, "/");

        var frame = DrawFrame(chat);
        Assert.Contains("model", frame);
        Assert.Contains("mcp", frame);
        Assert.Contains("session", frame);
        Assert.Contains("Commands", frame);
    }

    [Fact]
    public async Task Slash_m_Filters_Popup_To_Matching_Commands()
    {
        using var chat = await CreateChat();

        Type(chat, "/m");

        var frame = DrawFrame(chat);
        Assert.Contains("› mcp", frame);
        Assert.Contains("model", frame);
        Assert.DoesNotContain("› session", frame);
    }

    [Fact]
    public async Task Tab_On_Model_Opens_Argument_Menu_With_Provider_Models()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Tab);

        var frame = DrawFrame(chat);
        Assert.Contains("deepseek-official/deepseek-v4-flash", frame);

        Type(chat, "pro");
        frame = DrawFrame(chat);
        Assert.Contains("› deepseek-official/deepseek-v4-pro", frame);
        Assert.DoesNotContain("› deepseek-official/deepseek-v4-flash", frame);
    }

    [Fact]
    public async Task Esc_From_Argument_Returns_To_Command_List()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Escape);

        var frame = DrawFrame(chat);
        Assert.Contains("Commands", frame);
        Assert.Contains("mcp", frame);
    }

    /** 首 token 不是已知命令时按"用户写错了"处理: 整行作为普通消息发给模型, 不再报 unknown command。 */
    [Fact]
    public async Task Enter_On_Unknown_Command_Sends_It_As_Message()
    {
        using var chat = await CreateChat();

        Type(chat, "/xyz");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => SentUserTexts().Contains("/xyz"));

        var frame = DrawFrame(chat);
        Assert.DoesNotContain("unknown command", frame);
        Assert.DoesNotContain("> /xyz", frame);
        Assert.Contains(SentUserTexts(), text => text == "/xyz");
    }

    [Fact]
    public async Task Gpu_Lists_Adapters_And_Persists_Selection()
    {
        using var chat = await CreateChat();

        Type(chat, "/gpu");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("gpu: current = auto (system default)"));

        var frame = DrawFrame(chat);
        Assert.Contains("gpu: current = auto (system default)", frame);
        Assert.Contains("usage: /gpu <number>", frame);

        // 无显卡环境(纯 CI)只验证列表分支; 有卡环境继续验证选卡落盘。
        var adapters = GpuCatalog.ListAdapters();
        if (adapters.Count == 0)
            return;

        Type(chat, "/gpu 1");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("gpu: selected"));

        // 选卡值由 GpuCatalog.SelectionIdOf 决定: Linux 用 PCI slot(同名多卡可区分), Windows 用显示名
        var stored = GpuCatalog.SelectionIdOf(adapters[0]);
        frame = DrawFrame(chat);
        Assert.Contains($"gpu: selected {stored}", frame);
        // 与 GUI 设置页读同一个键(plugins.@deepseek-ai/dsh-gui 的 gpu.adapter)。
        Assert.Equal(stored, GpuCatalog.LoadSelectedAdapter(HarnessHome.Resolve(_homeDir)));
    }

    [Fact]
    public async Task Gpu_Rejects_Invalid_Selection()
    {
        using var chat = await CreateChat();

        Type(chat, "/gpu 999");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("gpu: invalid selection '999'"));

        var frame = DrawFrame(chat);
        Assert.Contains("gpu: invalid selection '999'", frame);
        Assert.Equal(GpuCatalog.AutoAdapter, GpuCatalog.LoadSelectedAdapter(HarnessHome.Resolve(_homeDir)));
    }

    [Fact]
    public async Task CtrlC_Twice_Requests_Exit_With_Status_Hint()
    {
        using var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.C);
        var frame = DrawFrame(chat);
        Assert.Contains("再按一次 Ctrl+C 退出", frame);
        Assert.False(chat.ExitRequested);

        PressCtrl(chat, ConsoleKey.C);
        Assert.True(chat.ExitRequested);
    }

    [Fact]
    public async Task CtrlX_Then_S_Opens_Session_Candidates()
    {
        using var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.S);

        var frame = DrawFrame(chat);
        Assert.Contains("Session id or title", frame);
    }

    /** 命令阶段 Tab 选中命令并列出候选; 参数阶段由 Enter 采纳候选并回填命令, 再按一次才发送。 */
    [Fact]
    public async Task Tab_On_Reasoning_Lists_Efforts_And_Confirms_Command()
    {
        using var chat = await CreateChat((ctx, commands) =>
        {
            _ = new LlmRuntime(ctx).RegisterAdapter(["deepseek-official"], new EffortAdapter());
            _ = ReasoningCommand.Register(ctx);
        });

        Type(chat, "/reasoning");
        Press(chat, ConsoleKey.Tab);

        var frame = DrawFrame(chat);
        Assert.Contains("Reasoning effort", frame);
        Assert.Contains("low", frame);
        Assert.Contains("high", frame);

        Press(chat, ConsoleKey.DownArrow);
        Press(chat, ConsoleKey.Enter);

        frame = DrawFrame(chat);
        Assert.Contains("> /reasoning high", frame);
        Assert.DoesNotContain("reasoning effort set to high", frame);

        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("reasoning effort set to high"));

        frame = DrawFrame(chat);
        Assert.Contains("reasoning effort set to high", frame);
    }

    [Fact]
    public async Task Model_Label_Updates_After_Model_Switch()
    {
        using var chat = await CreateChat((ctx, commands) =>
        {
            _ = new LlmRuntime(ctx).RegisterAdapter(["deepseek-official"], new EffortAdapter());
            _ = ModelCommand.Register(ctx, HarnessHome.Resolve(_homeDir));
        }, model: "m-a", stubModelCommand: false);

        var frame = DrawFrame(chat);
        Assert.Contains("model: deepseek-official/m-a", frame);

        Type(chat, "/model deepseek-official/m-b");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("model: deepseek-official/m-b"));

        frame = DrawFrame(chat);
        Assert.Contains("model: deepseek-official/m-b", frame);
        Assert.Contains("deepseek-official · m-b", frame);
        Assert.DoesNotContain("m-a", frame);
    }

    [Fact]
    public async Task Long_Command_Output_Folds_And_Expands()
    {
        var rows = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"row-{index} {new string('x', 40)}"));
        using var chat = await CreateChat((ctx, commands) => _ = commands.Register(new CommandDefinition
        {
            Name = "dump",
            Description = "Dump long output",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success(rows)),
        }));

        Type(chat, "/dump");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("row-1"));

        var frame = DrawFrame(chat);
        Assert.Contains("row-1", frame);
        Assert.DoesNotContain("row-9", frame);

        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);

        frame = DrawFrame(chat);
        Assert.Contains("row-9", frame);
    }


    /** 回车在命令上有候选时=选中: "/model" 回车应进入参数浮层(面板), 而不是执行命令打印模型列表。 */
    [Fact]
    public async Task Enter_On_Model_Opens_Argument_Menu_Instead_Of_Executing()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("Provider/model", frame);
        Assert.False(chat.ExitRequested);
    }

    /** 浮层里选中命令只回填输入行, 不发送执行。 */
    [Fact]
    public async Task Enter_On_Highlighted_Command_Selects_It()
    {
        using var chat = await CreateChat();

        Type(chat, "/");
        Press(chat, ConsoleKey.DownArrow);
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("> /model", frame);
        Assert.Contains("Provider/model", frame);
    }

    /** 部分输入的命令由浮层补全: 只回填不执行(防 "/m" 直接执行 "/mcp")。 */
    [Fact]
    public async Task Enter_On_Partial_Command_Completes_Without_Executing()
    {
        using var chat = await CreateChat();

        Type(chat, "/m");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("> /mcp", frame);
        Assert.DoesNotContain("mcp command ran", frame);
    }

    /** 参数由浮层选中只回填完整命令(不执行), 再按一次回车才发送。 */
    [Fact]
    public async Task Argument_Selection_Fills_Command_And_Second_Enter_Sends()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("Provider/model", frame);

        Press(chat, ConsoleKey.Enter);

        frame = DrawFrame(chat);
        Assert.Contains("> /model deepseek-official/deepseek-v4-flash", frame);
        Assert.DoesNotContain("Provider/model", frame);

        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => !DrawFrame(chat).Contains("> /model"));

        frame = DrawFrame(chat);
        Assert.DoesNotContain("> /model", frame);
    }

    /** 参数面板: 进入参数阶段即列出参数含义与必填标记; 自由文本参数无候选也不画空浮层。 */
    [Fact]
    public async Task Argument_Panel_Lists_Parameter_Meanings()
    {
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "provdemo",
            Description = "Demo provider command",
            ArgumentSchemas =
            [
                new CommandArgumentSchema("name", "text", "Provider name", Required: true),
                new CommandArgumentSchema("base-url", "text", "Base URL", Flag: "--base-url", Required: true),
            ],
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "/provdemo");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("Provider name", frame);
        Assert.Contains("Base URL", frame);
        Assert.Contains("(必填)", frame);
        Assert.Contains("▸ name", frame);
        Assert.DoesNotContain("(空)", frame);

        Type(chat, "kilo");
        frame = DrawFrame(chat);
        Assert.Contains("= kilo", frame);

        Press(chat, ConsoleKey.Enter);

        // 回车采纳 name 后落到下一条必填参数, 面板继续留在参数阶段。
        frame = DrawFrame(chat);
        Assert.Contains("▸ --base-url", frame);
        Assert.Contains("(必填)", frame);

        Type(chat, "https://example.com");
        Press(chat, ConsoleKey.Enter);

        // 必填参数全部填完 → 回填完整命令但先不发送(浮层已关)。
        frame = DrawFrame(chat);
        Assert.Contains("> /provdemo kilo --base-url https://example.com", frame);
        Assert.DoesNotContain("(必填)", frame);

        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => !DrawFrame(chat).Contains("> /provdemo"));

        frame = DrawFrame(chat);
        Assert.DoesNotContain("> /provdemo", frame);
    }

    /** Tab 在所有参数之间循环(含可选), 采纳当前值后前进, 不结束命令也不退回上一级菜单; 文案为"下一个参数"。 */
    [Fact]
    public async Task Tab_Cycles_Through_All_Arguments()
    {
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "/provider");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "add");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "acme");
        Press(chat, ConsoleKey.Tab);

        var frame = DrawFrame(chat);
        Assert.Contains("▸ --base-url", frame);
        Assert.Contains("Tab 下一个参数", frame);

        Press(chat, ConsoleKey.Tab);
        frame = DrawFrame(chat);
        Assert.Contains("▸ --api-key", frame);

        // api-key 留空也能继续走(采纳失败不阻塞), 且可选参数同样在循环里。
        Press(chat, ConsoleKey.Tab);
        frame = DrawFrame(chat);
        Assert.Contains("▸ --type", frame);

        Press(chat, ConsoleKey.Tab);
        frame = DrawFrame(chat);
        Assert.Contains("▸ --model-ids", frame);

        Press(chat, ConsoleKey.Tab);
        frame = DrawFrame(chat);
        Assert.Contains("▸ name", frame);
        Assert.Contains("= acme", frame);
    }

    /** 没有任何匹配时不画空浮层。 */
    [Fact]
    public async Task No_Popup_When_Root_Query_Matches_Nothing()
    {
        using var chat = await CreateChat();

        Type(chat, "/zzz");

        var frame = DrawFrame(chat);
        Assert.DoesNotContain("无匹配", frame);
        Assert.DoesNotContain("(空)", frame);
    }

    /** 数百模型的候选浮层: 标题给出位置/总数, 输入即筛选, PgUp/PgDn 翻页。 */
    [Fact]
    public async Task Model_Menu_Filters_And_Pages_Through_Many_Models()
    {
        SeedModels(400);
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("1/400", frame);

        Type(chat, "0033");
        frame = DrawFrame(chat);
        Assert.Contains("1/1", frame);
        Assert.Contains("deepseek-official/model-0033", frame);

        Press(chat, ConsoleKey.Backspace);
        Press(chat, ConsoleKey.Backspace);
        Press(chat, ConsoleKey.Backspace);
        Press(chat, ConsoleKey.Backspace);
        frame = DrawFrame(chat);
        Assert.Contains("1/400", frame);

        Press(chat, ConsoleKey.PageDown);
        frame = DrawFrame(chat);
        var position = Regex.Match(frame, @"(\d+)/400");
        Assert.True(position.Success, "PgDn 后标题应带位置计数");
        Assert.True(int.Parse(position.Groups[1].Value) > 10,
            $"PgDn 应跨过一页(>10), 实际 {position.Groups[1].Value}");
    }

    /** 浮层里 Home/End 跳候选首/尾。 */
    [Fact]
    public async Task Home_End_Jump_Candidates_Inside_Popup()
    {
        SeedModels(400);
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Enter);

        Press(chat, ConsoleKey.End);
        var frame = DrawFrame(chat);
        Assert.Contains("400/400", frame);

        Press(chat, ConsoleKey.Home);
        frame = DrawFrame(chat);
        Assert.Contains("1/400", frame);
    }

    /** 命令 + 提示词: 命令结束后提示词作为下一步用户消息进入会话。 */
    [Fact]
    public async Task Command_Followup_Prompt_Becomes_Next_User_Message()
    {
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "cmddemo",
            Description = "Command with prompt",
            AcceptsPrompt = true,
            Handler = invocation => Task.FromResult<CommandResult>(new CommandResult.Success(
                "cmddemo done",
                FollowupPrompt: invocation.RawInput.Trim())),
        }));

        Type(chat, "/cmddemo 帮我重构");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => SentUserTexts().Contains("帮我重构"));

        Assert.Contains("帮我重构", SentUserTexts());
    }

    /** 命令失败也照发提示词(当用户写错了)。 */
    [Fact]
    public async Task Failed_Command_Still_Sends_Prompt()
    {
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "faildemo",
            Description = "Failing command",
            AcceptsPrompt = true,
            Handler = invocation => Task.FromResult<CommandResult>(new CommandResult.Error(
                "faildemo failed",
                invocation.RawInput.Trim())),
        }));

        Type(chat, "/faildemo 继续做");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => SentUserTexts().Contains("继续做"));

        Assert.Contains("继续做", SentUserTexts());
        Assert.Contains("faildemo failed", DrawFrame(chat));
    }

    /** Ctrl+P: 已输入提示词后唤出"能跟提示词"的命令选单, 选中后命令落到首 token、原文本成为提示词。 */
    [Fact]
    public async Task CtrlP_Prepends_Selected_Command_To_Typed_Prompt()
    {
        using var chat = await CreateChat((_, commands) =>
        {
            commands.Register(new CommandDefinition
            {
                Name = "promptdemo",
                Description = "Prompt-accepting command",
                AcceptsPrompt = true,
                ArgumentSchemas = [new CommandArgumentSchema("name", "select", "Pick one", Choices: ["alpha", "beta"])],
                Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
            });
            commands.Register(new CommandDefinition
            {
                Name = "argdemo",
                Description = "Argument-taking command without prompt support",
                ArgumentSchemas = [new CommandArgumentSchema("value", "text", "Value")],
                Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
            });
        });

        Type(chat, "帮我重构");
        PressCtrl(chat, ConsoleKey.P);

        var frame = DrawFrame(chat);
        Assert.Contains("promptdemo", frame);
        Assert.DoesNotContain("argdemo", frame);

        Press(chat, ConsoleKey.Enter);
        frame = DrawFrame(chat);
        Assert.Contains("Pick one", frame);

        Press(chat, ConsoleKey.Enter);
        frame = DrawFrame(chat);
        Assert.Contains("> /promptdemo alpha 帮我重构", frame);
        Assert.DoesNotContain("Pick one", frame);
    }

    /** 取消 Ctrl+P 浮层时提示词要回到输入行, 不被吞掉。 */
    [Fact]
    public async Task CtrlP_Escape_Restores_Typed_Prompt()
    {
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "promptdemo",
            Description = "Prompt-accepting command",
            AcceptsPrompt = true,
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "帮我重构");
        PressCtrl(chat, ConsoleKey.P);
        Press(chat, ConsoleKey.Escape);

        var frame = DrawFrame(chat);
        Assert.Contains("> 帮我重构", frame);
    }

    /** 输入行为空时 Home/End 直达记录头/尾; 有内容时仍作用于输入光标。 */
    [Fact]
    public async Task Home_End_Jump_Transcript_When_Input_Empty()
    {
        var rows = string.Join('\n', Enumerable.Range(1, 40).Select(index => $"row-{index:D2}"));
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "dump",
            Description = "Dump long output",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success(rows)),
        }));

        Type(chat, "/dump");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("row-40"));

        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("row-40", frame);
        Assert.DoesNotContain("row-01", frame);

        Press(chat, ConsoleKey.Home);
        frame = DrawFrame(chat);
        Assert.Contains("row-01", frame);

        Press(chat, ConsoleKey.End);
        frame = DrawFrame(chat);
        Assert.Contains("row-40", frame);
    }

    /** 滚轮向上滚动记录(OpenTUI 同款: 每格基础增量 × 加速倍数, 分数累积)。 */
    [Fact]
    public async Task Mouse_Wheel_Scrolls_Transcript()
    {
        var rows = string.Join('\n', Enumerable.Range(1, 40).Select(index => $"row-{index:D2}"));
        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "dump",
            Description = "Dump long output",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success(rows)),
        }));

        Type(chat, "/dump");
        Press(chat, ConsoleKey.Enter);
        await SettleAsync(chat, () => DrawFrame(chat).Contains("row-40"));
        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);

        var layout = LayoutEngine.Calculate(100, 30);
        var frame = DrawFrame(chat);
        Assert.Contains("row-40", frame);

        for (var notch = 0; notch < 12; notch++)
        {
            chat.HandleMouseWheel(1f, 20, 10, layout);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        frame = DrawFrame(chat);
        Assert.DoesNotContain("row-40", frame);
        Assert.Contains("row-01", frame);
    }

    /** /provider add 的 name 候选来自 models.dev 目录缓存; 没有适配器的 provider 不出现。 */
    [Fact]
    public async Task Provider_Add_Name_Candidates_Come_From_Catalog_Cache()
    {
        var cache = ProviderCatalog.CacheFile(HarnessHome.Resolve(_homeDir));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, ProviderCatalogTests.Fixture);

        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "/provider");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "add");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("Provider name", frame);
        Assert.Contains("zhipuai-coding-plan", frame);
        Assert.DoesNotContain("azure-openai", frame);
    }

    /** 选中目录 provider 后只补 api-key: baseUrl/type/models 已按目录带出。 */
    [Fact]
    public async Task Provider_Add_Catalog_Selection_Prefills_BaseUrl_Type_And_Models()
    {
        var cache = ProviderCatalog.CacheFile(HarnessHome.Resolve(_homeDir));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, ProviderCatalogTests.Fixture);

        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "/provider");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "add");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "zhipu");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        // name/base-url/type/model-ids 都已由目录预置满足 → 直接落在唯一还需输入的必填 api-key。
        Assert.Contains("▸ --api-key", frame);
        Assert.Contains("✓ --base-url", frame);
        Assert.Contains("https://open.bigmodel.cn/api/coding/paas/v4", frame);
        Assert.Contains("(自动填好)", frame);
        Assert.Contains("(必填)", frame);
        // 模型列表用 <all> 标记代替长串模型名。
        Assert.Contains("<all>", frame);
        Assert.DoesNotContain("glm-5.3", frame);
        Assert.Contains("Tab 下一个参数", frame);
    }

    /** 选中协议族/自定义条目: 视为自定义端点, 只预置 type, provider 名仍要用户填。 */
    [Fact]
    public async Task Provider_Add_Protocol_Entry_Presets_Type_And_Keeps_Name()
    {
        var cache = ProviderCatalog.CacheFile(HarnessHome.Resolve(_homeDir));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, ProviderCatalogTests.Fixture);

        using var chat = await CreateChat((_, commands) => commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        }));

        Type(chat, "/provider");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "add");
        Press(chat, ConsoleKey.Enter);
        Type(chat, "custom(anthropic-messages)");
        Press(chat, ConsoleKey.Enter);

        var frame = DrawFrame(chat);
        Assert.Contains("▸ name", frame);
        Assert.Contains("✓ --type", frame);
        Assert.Contains("= anthropic", frame);
        Assert.Contains("> /provider add", frame);
    }

    /** 已交给 agent 的用户文本: 计划中的下一轮(未开工时在 inbox)与会话里已落盘的用户消息都算。 */
    private List<string> SentUserTexts()
        => [.. _lastAgent!.Inbox.NextTurn
                .Concat(_lastAgent.Inbox.NextStep)
                .Select(MessageText)
                .Concat(_lastAgent.Session.SnapshotEvents()
                    .Select(sessionEvent => sessionEvent.Data)
                    .OfType<UserMessagePayload>()
                    .Select(payload => MessageText(payload.Message)))];

    /**
     * 斜杠命令是 async void, 测试拿不到完成信号: 轮询到命令的可见效果出现为止。
     * 这些命令都是本地的(菜单/设置), 通常几十毫秒内完成, 比固定 300ms 等待快且不依赖机器速度。
     */
    private static async Task SettleAsync(ChatWindow chat, Func<bool> ready)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            chat.DrainUi();
            if (ready())
                return;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static string MessageText(UserMessage message)
        => string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));

    /** 造出 count 个模型的 provider 设置(菜单候选与翻页的规模)。 */
    private void SeedModels(int count)
    {
        Directory.CreateDirectory(_homeDir);
        var models = string.Join('\n', Enumerable.Range(0, count).Select(index =>
            $"      model-{index:0000}:\n        name: model-{index:0000}"));
        File.WriteAllText(Path.Combine(_homeDir, "settings.yaml"),
            "global_default_model: null\nproviders:\n  deepseek-official:\n    type: openai-compatible\n"
            + "    options:\n      baseUrl: http://127.0.0.1:1/v1\n    models:\n" + models + "\n");
    }

    private sealed class EffortAdapter : LlmAdapter
    {
        public override LlmProviderInfo ProviderInfo { get; } = new("deepseek-official", "deepseek-official");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new("deepseek-official", model, model,
                Reasoning: new LlmModelReasoningInfo(
                [
                    new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"),
                    new LlmReasoningEffortInfo(ReasoningEffortId.Create("high"), "High"),
                ]));
    }

    private async Task<ChatWindow> CreateChat(Action<Context, CommandsService>? configure = null, string model = "deepseek-v4-flash", bool stubModelCommand = true)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        // 会话里要能落到用户消息: agent loop 需要 systemPrompt 服务(真实宿主由插件提供)。
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        if (stubModelCommand)
            RegisterCommand(commands, "model", "List or switch model");
        RegisterCommand(commands, "mcp", "Manage MCP servers");
        RegisterCommand(commands, "session", "Manage sessions");
        configure?.Invoke(ctx, commands);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", model)));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        _lastAgent = agent;
        var home = HarnessHome.Resolve(_homeDir);
        return new ChatWindow(ctx, agent, home);
    }

    private static void RegisterCommand(CommandsService commands, string name, string description)
        => commands.Register(new CommandDefinition
        {
            Name = name,
            Description = description,
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        });

    private static void Type(ChatWindow chat, string text)
    {
        foreach (var character in text)
            chat.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
    }

    private static void Press(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static void PressCtrl(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, true));

    private static string DrawFrame(ChatWindow chat)
    {
        var layout = LayoutEngine.Calculate(100, 30);
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, layout);
        var lines = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            var chars = new char[grid.Width];
            for (var x = 0; x < grid.Width; x++)
                chars[x] = grid[x, y].Character;
            lines.Add(new string(chars).Replace("\0", ""));
        }

        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }
}