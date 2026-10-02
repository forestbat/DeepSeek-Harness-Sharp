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
    public async Task UnpositionedSections_AppendAfterPositionedInRegistrationOrder()
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
            ["deployment:persona", "tool:bash", "zz:free", "aa:free"],
            names);
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
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        using var first = prompt.Context(PromptContext.Literal("board:coordination", "board"));
        using var second = prompt.Context(PromptContext.Literal("approval:policy", "approval"));

        var assembly = await prompt.Assemble(new AssembleContext());

        Assert.Equal(["approval:policy", "board:coordination"], assembly.Contexts.Select(context => context.Name).ToList());
    }
}
