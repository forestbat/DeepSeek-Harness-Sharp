using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 提示词命名段排序(§5): 脊柱定内置基准序, After/Before 自定位, 未定位段按注册序追加, 成环按注册序打破并 WARN。 */
public sealed class PromptOrderingTests
{
    private static async Task<IReadOnlyList<string>> AssembleSectionNames(
        SystemPrompt prompt,
        IReadOnlyList<PromptSection> sections,
        IReadOnlyList<PromptContext>? contexts = null)
    {
        var disposables = sections.Select(prompt.Section).ToList();
        disposables.AddRange((contexts ?? []).Select(prompt.Context));
        var assembly = await prompt.Assemble(new AssembleContext());
        foreach (var disposable in disposables)
            disposable.Dispose();
        return assembly.Sections.Select(section => section.Name).ToList();
    }

    /**
     * persona 段存在但默认文本为空: 内置 persona 已删除, persona 由 dsh-persona 插件(默认注册空串)接管。
     * 这条守门用例防止将来有人"顺手"再塞一段内置文本回去 —— 那会改变每一次请求的提示词。
     */
    [Fact]
    public async Task PersonaSection_ExistsButIsEmptyByDefault()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        using var registration = prompt.Section(PromptSection.Literal("probe", "x"));

        var assembly = await prompt.Assemble(new AssembleContext());

        var persona = Assert.Single(assembly.Sections, section => section.Name == "deployment:persona");
        Assert.Equal("", persona.Text);
    }

    [Fact]
    public async Task SpineSections_OrderBySpineRegardlessOfRegistrationOrder()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });

        var names = await AssembleSectionNames(prompt,
        [
            PromptSection.Literal("tool:grep", "grep"),
            PromptSection.Literal("tool:bash", "bash"),
            PromptSection.Literal("agent-instructions", "instructions"),
        ]);

        Assert.Equal(
            ["deployment:persona", "agent-instructions", "tool:bash", "tool:grep"],
            names);
    }

    [Fact]
    public async Task ExplicitConstraints_PositionSectionBetweenSpineEntries()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });

        var names = await AssembleSectionNames(prompt,
        [
            new PromptSection("custom:middle", _ => "middle", After: ["tool:bash"], Before: ["tool:grep"]),
            PromptSection.Literal("tool:grep", "grep"),
            PromptSection.Literal("tool:bash", "bash"),
        ]);

        Assert.Equal(
            ["deployment:persona", "tool:bash", "custom:middle", "tool:grep"],
            names);
    }

    [Fact]
    public async Task UnpositionedSections_AppendAfterPositionedInNameOrder()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });

        var names = await AssembleSectionNames(prompt,
        [
            PromptSection.Literal("zz:free", "free"),
            PromptSection.Literal("tool:bash", "bash"),
            PromptSection.Literal("aa:free", "free"),
        ]);

        Assert.Equal(
            ["deployment:persona", "tool:bash", "aa:free", "zz:free"],
            names);
    }

    /** 插件异步加载会让注册序漂移; 同样的段落在两次组装里必须逐字节相同, 否则前缀缓存会在换位处失效。 */
    [Fact]
    public async Task UnpositionedSections_AreStableAcrossRegistrationOrder()
    {
        var forward = new SystemPrompt(new Context(), new SystemPromptConfig { IncludeHarnessIdentity = false });
        _ = forward.Section(PromptSection.Literal("aa:free", "a"));
        _ = forward.Section(PromptSection.Literal("zz:free", "z"));

        var backward = new SystemPrompt(new Context(), new SystemPromptConfig { IncludeHarnessIdentity = false });
        _ = backward.Section(PromptSection.Literal("zz:free", "z"));
        _ = backward.Section(PromptSection.Literal("aa:free", "a"));

        Assert.Equal(
            PromptRender.RenderPrompt(await forward.Assemble(new AssembleContext())),
            PromptRender.RenderPrompt(await backward.Assemble(new AssembleContext())));
    }

    [Fact]
    public async Task ConstraintCycle_BreaksByRegistrationOrderAndWarns()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });

        var names = await AssembleSectionNames(prompt,
        [
            new PromptSection("cycle:a", _ => "a", After: ["cycle:b"]),
            new PromptSection("cycle:b", _ => "b", After: ["cycle:a"]),
        ]);

        Assert.Equal(["deployment:persona", "cycle:a", "cycle:b"], names);
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("cycle")
            && message.Text.Contains("cycle:a"));
    }

    [Fact]
    public async Task Contexts_FollowTheirOwnSpine()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false, FoldContextsIntoSystem = false });
        using var first = prompt.Context(PromptContext.Literal("board:coordination", "board"));
        using var second = prompt.Context(PromptContext.Literal("approval:policy", "approval"));

        var assembly = await prompt.Assemble(new AssembleContext());

        Assert.Equal(["approval:policy", "board:coordination"], assembly.Contexts.Select(context => context.Name).ToList());
    }
}
