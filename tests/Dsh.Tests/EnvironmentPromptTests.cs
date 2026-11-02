using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class EnvironmentPromptTests
{
    private static SystemPrompt Prompt(bool enabled, string persona = "")
        => new(new Context(), new SystemPromptConfig
        {
            IncludeHarnessIdentity = false,
            IncludeEnvironment = enabled,
            Persona = persona,
        });

    [Fact]
    public async Task Environment_IsAppendedToPersona()
    {
        var prompt = Prompt(enabled: true, persona: "persona body");
        prompt.Section(PromptSection.Literal("agent-instructions", "agents"));

        var assembly = await prompt.Assemble(new AssembleContext());

        Assert.Equal(
            ["deployment:persona", "agent-instructions"],
            assembly.Sections.Select(section => section.Name).ToList());
        var text = assembly.Sections.Single(section => section.Name == "deployment:persona").Text;
        Assert.StartsWith("persona body", text);
        Assert.Contains("# Environment", text);
        Assert.Contains("<env>", text);
        Assert.Contains("Project config: AGENTS.md", text);
    }

    [Fact]
    public async Task Environment_CanBeDisabled()
    {
        var prompt = Prompt(enabled: false, persona: "persona body");

        var assembly = await prompt.Assemble(new AssembleContext());

        var text = assembly.Sections.Single(section => section.Name == "deployment:persona").Text;
        Assert.Equal("persona body", text);
        Assert.DoesNotContain("# Environment", text);
    }

    [Fact]
    public async Task Environment_HasNoKiloOrUserPathOrModelFallback()
    {
        var prompt = Prompt(enabled: true);

        var assembly = await prompt.Assemble(new AssembleContext());

        var text = assembly.Sections.Single(section => section.Name == "deployment:persona").Text;
        Assert.DoesNotContain("kilo", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("k6789", text);
        Assert.DoesNotContain("deepseek-flash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global config:", text);
    }
}
