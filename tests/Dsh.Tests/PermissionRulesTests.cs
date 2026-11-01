using System.Text.Json;
using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class PermissionRulesTests
{
    [Fact]
    public void StaticYamlScalarAndListTools_PreserveDecisionsAcrossRoundTrip()
    {
        var rules = RuleMapYaml.Parse("""
            one-tool:
              tool: read
              args: { mode: 'preview*' }
            two-tools:
              tool: [write, edit]
              args: { content: '*public*' }
            """);
        var roundTrip = RuleMapYaml.Parse(RuleMapYaml.Format(rules));
        foreach (var map in new[] { rules, roundTrip })
        {
            var policy = new PermissionRules { Whitelist = map };
            Assert.Equal("allow", Decide(policy, "read", new { mode = "preview only" }).Decision);
            Assert.Equal("allow", Decide(policy, "edit", new { content = "public text" }).Decision);
            Assert.Equal("notify", Decide(policy, "write", new { content = "private text" }).Decision);
            Assert.Equal("notify", Decide(policy, "read", new { mode = 123 }).Decision);
        }
    }

    [Theory]
    [InlineData("- tool:write")]
    [InlineData("broken: tool:write")]
    [InlineData("broken: { tool: [] }")]
    [InlineData("broken: { tool: { name: read } }")]
    [InlineData("broken: { args: { path: '*.txt' } }")]
    [InlineData("broken: { args: { file_path: '*.txt' } }")]
    [InlineData("broken: { args: { mode: [first, second] } }")]
    [InlineData("broken: { intent: delete }")]
    [InlineData("broken: { dialect: python }")]
    [InlineData("broken: { unknown: value }")]
    public void InvalidAndObsoleteRuleFormats_AreRejected(string yaml)
        => Assert.ThrowsAny<Exception>(() => RuleMapYaml.Parse(yaml));

    [Fact]
    public void AllDimensionsAreConjunctive_AndDenyPrecedesAutomaticAndExplicitAllow()
    {
        var cwd = Path.GetFullPath(".");
        var policy = new PermissionRules
        {
            AutoApprove = true,
            Blacklist = RuleMapYaml.Parse("""
                private-write:
                  tool: [write, edit]
                  path: 'src/**'
                  intent: write
                  args: { mode: apply, content: '*private*' }
                """),
            Whitelist = new() { ["allow-all"] = new() },
        };
        var input = new PermissionInput("write", JsonSerializer.SerializeToElement(new { mode = "apply", content = "private text" }),
            cwd, Path.Combine(cwd, "src", "nested", "file.txt"), "write");
        var denied = PermissionEngine.Decide(policy, input, Path.GetFullPath);
        Assert.Equal("deny", denied.Decision);
        Assert.Equal("private-write", denied.Match!.RuleId);
        Assert.Equal("allow", PermissionEngine.Decide(policy, input with { Tool = "read" }, Path.GetFullPath).Decision);
        Assert.Equal("allow", PermissionEngine.Decide(policy, input with { Intent = "read" }, Path.GetFullPath).Decision);
        Assert.Equal("allow", PermissionEngine.Decide(policy, input with { Path = Path.Combine(cwd, "outside.txt") }, Path.GetFullPath).Decision);
        Assert.Equal("allow", PermissionEngine.Decide(policy, input with { Arguments = JsonSerializer.SerializeToElement(new { mode = "preview", content = "private text" }) }, Path.GetFullPath).Decision);
    }

    [Fact]
    public void UnknownActualPathOrIntent_CannotAcquireAFileWhitelistGrant()
    {
        var policy = new PermissionRules
        {
            Whitelist = RuleMapYaml.Parse("source: { path: 'src/**', intent: read }"),
        };
        var input = new PermissionInput("read", JsonSerializer.SerializeToElement(new { file_path = "src/file.txt" }), Path.GetFullPath("."));
        Assert.Equal("notify", PermissionEngine.Decide(policy, input, Path.GetFullPath).Decision);
        Assert.Equal("notify", PermissionEngine.Decide(policy, input with { Path = Path.GetFullPath("src/file.txt") }, Path.GetFullPath).Decision);
        Assert.Equal("allow", PermissionEngine.Decide(policy, input with { Path = Path.GetFullPath("src/file.txt"), Intent = "read" }, Path.GetFullPath).Decision);
    }

    [Theory]
    [InlineData("bash", "sudo env NAME=value /bin/rm -fr /*")]
    [InlineData("bash_persistent", "true && rm /* -r -f")]
    public void CommandMatcher_UsesExistingTokenWrapperAndFlagSemantics(string tool, string command)
    {
        var policy = new PermissionRules { AutoApprove = true, Blacklist = RuleMapYaml.Parse("""
            dangerous-command:
              dialect: posix
              args: { command: 'rm -rf /*' }
            """) };
        Assert.Equal("deny", Decide(policy, tool, new { command }).Decision);
        Assert.Equal("allow", Decide(policy, tool, new { command = "printf harmless" }).Decision);
    }

    [Theory]
    [InlineData("src/*", "src/file.txt", true)]
    [InlineData("src/*", "src/nested/file.txt", false)]
    [InlineData("src/**", "src/nested/file.txt", true)]
    [InlineData("src/**/file.txt", "src/file.txt", true)]
    [InlineData("src/**/file.txt", "src/nested/file.txt", true)]
    public void PathWildcards_PreserveSegmentBoundaries(string pattern, string path, bool allowed)
    {
        var policy = new PermissionRules { Whitelist = new() { ["read-source"] = new() { Path = pattern, Intent = "read" } } };
        var input = new PermissionInput("read", default, Path.GetFullPath("."), Path.GetFullPath(path), "read");
        Assert.Equal(allowed ? "allow" : "notify", PermissionEngine.Decide(policy, input, Path.GetFullPath).Decision);
    }

    private static PermissionDecision Decide(PermissionRules policy, string tool, object arguments)
        => PermissionEngine.Decide(policy, new PermissionInput(tool, JsonSerializer.SerializeToElement(arguments), Path.GetFullPath(".")), Path.GetFullPath);
}
