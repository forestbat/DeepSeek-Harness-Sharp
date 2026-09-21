using Dsh.Runtime;
using Dsh.AgentInstructions;
using Dsh.Boot;
using Dsh.Core;
using System.Runtime.CompilerServices;

namespace Dsh.Tests;

public sealed class AgentInstructionsTests
{
    private static (Context Ctx, SystemPrompt Prompt, IDisposable Registration) Compose(HarnessHome home, string cwd)
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig());
        ctx.SetOwn("harnessOptions", new HarnessOptions(home, cwd));
        var registration = new AgentInstructions.Plugin("@deepseek-ai/dsh-agent-instructions").Apply(ctx, null);
        return (ctx, prompt, registration);
    }

    [Fact]
    public async Task MergesAgentsMdFromProjectUpToHomeAndHarnessGlobal()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-agents-md", Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "sub");
        var home = new HarnessHome(Path.Combine(root, "home"));
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(home.Root);
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "root instructions");
            File.WriteAllText(Path.Combine(sub, "AGENTS.md"), "sub instructions");
            File.WriteAllText(Path.Combine(home.Root, "AGENTS.md"), "harness global instructions");

            var (_, prompt, registration) = Compose(home, sub); using var _reg = registration;
            var assembly = await prompt.Assemble(new AssembleContext());
            var section = assembly.Sections.Single(s => s.Name == "agent-instructions");

            var rootIdx = section.Text.IndexOf("root instructions", StringComparison.Ordinal);
            var subIdx = section.Text.IndexOf("sub instructions", StringComparison.Ordinal);
            var globalIdx = section.Text.IndexOf("harness global instructions", StringComparison.Ordinal);
            Assert.True(rootIdx >= 0 && subIdx > rootIdx && globalIdx > subIdx);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [NoInheritedAgentsMdFact]
    public async Task MissingAgentsMdSilentlySkips()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-agents-md", Guid.NewGuid().ToString("N"));
        var home = new HarnessHome(Path.Combine(root, "home"));
        Directory.CreateDirectory(root);
        try
        {
            var (_, prompt, registration) = Compose(home, root); using var _reg = registration;
            var assembly = await prompt.Assemble(new AssembleContext());
            var section = assembly.Sections.Single(s => s.Name == "agent-instructions");
            Assert.Equal("", section.Text);
            var rendered = PromptRender.RenderPrompt(assembly);
            Assert.DoesNotContain("Agent Instructions", rendered);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AppendsRulesFromSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-agents-md", Guid.NewGuid().ToString("N"));
        var home = new HarnessHome(Path.Combine(root, "home"));
        Directory.CreateDirectory(home.Root);
        try
        {
            File.WriteAllText(Path.Combine(home.Root, "settings.yaml"), """
                rules:
                  - 永远使用中文回复
                  - 不要写注释
                """);

            var (_, prompt, registration) = Compose(home, root); using var _reg = registration;
            var assembly = await prompt.Assemble(new AssembleContext());
            var section = assembly.Sections.Single(s => s.Name == "agent-instructions");
            Assert.Contains("## Rules", section.Text);
            Assert.Contains("- 永远使用中文回复", section.Text);
            Assert.Contains("- 不要写注释", section.Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/**
 * "缺失 AGENTS.md 就静默跳过"的前提是: 从临时目录到用户 profile 这条链上没有 AGENTS.md。
 * 开发者自己可能在那里放了全局指令(设计上会被并入), 此时跳过而不是假失败。
 */
public sealed class NoInheritedAgentsMdFactAttribute : FactAttribute
{
    public NoInheritedAgentsMdFactAttribute([CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : base(sourceFilePath, sourceLineNumber)
    {
        if (InheritedAgentsMdExists())
            Skip = "an AGENTS.md already exists between the temp directory and the user profile";
    }

    private static bool InheritedAgentsMdExists()
    {
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var directory = Path.GetFullPath(Path.GetTempPath());
        while (true)
        {
            if (File.Exists(Path.Combine(directory, "AGENTS.md")))
                return true;
            if (string.Equals(directory, profile, StringComparison.OrdinalIgnoreCase))
                return false;
            if (Directory.GetParent(directory) is not { } parent)
                return false;
            directory = parent.FullName;
        }
    }
}
