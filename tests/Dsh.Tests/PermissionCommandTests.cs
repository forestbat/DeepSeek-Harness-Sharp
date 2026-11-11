using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tools;
using Dsh.Subprocess;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed class PermissionCommandTests
{
    [Theory]
    [InlineData("rm -rf /*")]
    [InlineData("rm   -rf    /*")]
    [InlineData("rm -r -f /*")]
    [InlineData("rm -fr /*")]
    [InlineData("rm /* -rf")]
    [InlineData("'rm' \"-rf\" '/*'")]
    [InlineData("r''m -rf /*")]
    [InlineData("\\rm -rf /*")]
    [InlineData("/bin/rm -rf /*")]
    [InlineData("FOO=1 rm -rf /*")]
    [InlineData("sudo -u root rm -rf /*")]
    [InlineData("true; rm -rf /*")]
    [InlineData("true&&rm -rf /*")]
    [InlineData("false || rm -rf /*")]
    [InlineData("echo y | rm -rf /*")]
    [InlineData("echo $(rm -rf /*)")]
    [InlineData("echo \"$(rm -rf /*)\"")]
    [InlineData("echo `rm -rf /*`")]
    [InlineData("bash -c 'rm -rf /*'")]
    [InlineData("( rm -rf /* )")]
    [InlineData("ls\nrm -rf /*")]
    public void PosixPattern_MatchesBypassVariants(string command)
    {
        var pattern = ShellCommandPattern.Parse(" rm -rf /*", ShellDialect.Posix);

        Assert.True(pattern.Matches(ShellCommandSegments.Split(command, ShellDialect.Posix)));
    }

    [Theory]
    [InlineData("rm -r /tmp/build")]
    [InlineData("rm -rf ./build")]
    [InlineData("echo rm")]
    [InlineData("git status")]
    public void PosixPattern_IgnoresUnrelatedCommands(string command)
    {
        var pattern = ShellCommandPattern.Parse("rm -rf /*", ShellDialect.Posix);

        Assert.False(pattern.Matches(ShellCommandSegments.Split(command, ShellDialect.Posix)));
    }

    [Theory]
    [InlineData("remove-item -recurse C:\\")]
    [InlineData("& Remove-Item -Recurse 'C:\\'")]
    [InlineData("Get-ChildItem; Remove-Item  -Recurse C:\\")]
    [InlineData("Write-Output $(Remove-Item -Recurse C:\\)")]
    public void PowerShellPattern_MatchesCaseInsensitiveVariants(string command)
    {
        var pattern = ShellCommandPattern.Parse("Remove-Item -Recurse C:\\", ShellDialect.PowerShell);

        Assert.True(pattern.Matches(ShellCommandSegments.Split(command, ShellDialect.PowerShell)));
    }

    [Fact]
    public void NameOnlyPattern_BlocksTheCommandAnywhereInTheLine()
    {
        var pattern = ShellCommandPattern.Parse("shutdown", ShellDialect.Posix);

        Assert.True(pattern.Matches(ShellCommandSegments.Split("echo bye && /sbin/shutdown -h now", ShellDialect.Posix)));
        Assert.False(pattern.Matches(ShellCommandSegments.Split("echo shutdown-notes.txt", ShellDialect.Posix)));
    }


    public sealed class Bash : IDisposable
    {
        private readonly Context _ctx = new();
        private readonly ToolRuntime _tools;
        private readonly string _directory = TempTree.CreateDirectory("safety");

        public Bash()
        {
            _ = new SystemPrompt(_ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(_ctx);
            _ = new SubprocessService(_ctx);
            BashTool.Register(_ctx);
            var safety = new SafetySettings { AutoApprove = true, Blacklist = new()
                { ["deny-touch"] = new() { Dialect = "posix", Args = new() { ["command"] = $"touch {Canary}" } } } };
            _ = new SafetyPolicySource(_ctx, () => new HarnessSettings { Safety = safety });
        }

        private string Canary => Path.Combine(_directory, "canary");

        public void Dispose()
        {
            TempTree.Delete(_directory);
        }

        [Theory]
        [InlineData("touch   {0}")]
        [InlineData("'touch' \"{0}\"")]
        [InlineData("/usr/bin/env touch {0}")]
        [InlineData("true; touch {0}")]
        [InlineData("true && touch {0}")]
        [InlineData("echo $(touch {0})")]
        [InlineData("bash -c \"touch {0}\"")]
        public async Task Guard_BlocksVariantsBeforeTheShellRuns(string template)
        {
            var result = await RunAsync(string.Format(template, Canary));

            Assert.True(result.IsError);
            Assert.False(File.Exists(Canary));
        }

        [Fact]
        public async Task Guard_LetsUnrelatedCommandsRun()
        {
            var result = await RunAsync($"echo safe > {Path.Combine(_directory, "other")}");

            Assert.False(result.IsError);
        }

        private Task<ToolExecutionResult> RunAsync(string command)
        {
            if (!ShellProbe.Bash)
                Assert.Skip("bash is unavailable; skipping the shell-backed guard test");
            return _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = BashTool.ToolName,
                Arguments = JsonSerializer.SerializeToElement(new { command, description = "safety guard test" }, DshJson.Options),
                Signal = default,
            });
        }
    }

    public sealed class ShellDialectFamilies : IDisposable
    {
        private readonly Context _ctx = new();
        private readonly ToolRuntime _tools;
        private readonly List<IDisposable> _registrations = [];

        public ShellDialectFamilies()
        {
            _ = new SystemPrompt(_ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(_ctx);
            _ = new SubprocessService(_ctx);
            _registrations.AddRange([BashTool.Register(_ctx), PwshTool.Register(_ctx), PersistentBashTool.Register(_ctx), PersistentPwshTool.Register(_ctx)]);
        }

        public void Dispose()
        {
            foreach (var registration in _registrations)
                registration.Dispose();
        }

        /** The guard answers before any shell starts, so these cases need neither bash nor pwsh. */
        [Theory]
        [InlineData("posix", "bash_persistent", "touch canary", "touch canary")]
        [InlineData("posix", "bash", "touch canary", "true && touch canary")]
        [InlineData("powershell", "pwsh_persistent", "Remove-Item canary", "remove-item canary")]
        [InlineData("powershell", "pwsh", "Remove-Item canary", "Get-Date; Remove-Item canary")]
        public async Task ShellRule_BlocksEveryToolOfTheSameDialect(string dialect, string tool, string pattern, string command)
        {
            var safety = new SafetySettings { Blacklist = new()
                { ["deny-command"] = new() { Dialect = dialect, Args = new() { ["command"] = pattern } } } };
            _ = new SafetyPolicySource(_ctx, () => new HarnessSettings { Safety = safety });

            var result = await _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = tool,
                Arguments = JsonSerializer.SerializeToElement(new { command }, DshJson.Options),
                Signal = default,
            });

            Assert.True(result.IsError);
            Assert.Contains("blacklist", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
    }
}
