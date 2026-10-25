using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Tests;

public partial class ToolsTests
{
    private const string WindowsOnlySkipReason = "the tool is registered on Windows only";

    public sealed class WindowsPowerShell : IDisposable
    {
        private readonly ToolsTests _outer = new();

        public WindowsPowerShell()
        {
            WindowsPowerShellTool.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        private Task<ToolExecutionResult> Run(string command, string? workdir = null)
        {
            if (!OperatingSystem.IsWindows())
                Assert.Skip(WindowsOnlySkipReason);
            return _outer.Execute(WindowsPowerShellTool.ToolName, JsonSerializer.Serialize(new
            {
                command,
                description = "Run test command",
                workdir,
            }, DshJson.Options));
        }

        private static string StdoutOf(ToolExecutionResult result)
            => Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("stdout").GetProperty("text").GetString()!;

        [Fact]
        public async Task Command_RunsInWindowsPowerShellDesktopEdition()
        {
            var result = await Run("Write-Output \"$($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion.Major)\"");

            Assert.False(result.IsError, TextOf(result));
            Assert.Equal("Desktop 5", StdoutOf(result).Trim());
        }

        [Fact]
        public async Task DoubleQuotesAndNonAscii_SurviveTheCommandLine()
        {
            var result = await Run("Write-Output \"say \"\"hi\"\" 你好\"");

            Assert.False(result.IsError, TextOf(result));
            Assert.Equal("say \"hi\" 你好", StdoutOf(result).Trim());
        }

        [Fact]
        public async Task NonZeroExit_IsReportedWithStderr()
        {
            var result = await Run("[Console]::Error.WriteLine('oops'); exit 3");

            Assert.False(result.IsError);
            Assert.Equal(3, Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("exitCode").GetInt32());
            var text = TextOf(result);
            Assert.Contains("[stderr]", text);
            Assert.EndsWith("[exit code: 3]", text);
        }

        [Fact]
        public async Task Workdir_IsHonored()
        {
            var result = await Run("(Get-Location).Path", _outer._tempDir);

            Assert.False(result.IsError, TextOf(result));
            Assert.Equal(Path.GetFullPath(_outer._tempDir).TrimEnd('\\'), StdoutOf(result).Trim().TrimEnd('\\'), ignoreCase: true);
        }
    }

    public sealed class Wsl : IDisposable
    {
        private const long WslTimeoutMs = 60_000;

        private readonly ToolsTests _outer = new();

        public Wsl()
        {
            WslTool.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        private Task<ToolExecutionResult> Run(object arguments)
            => Run(arguments, requireLinux: true);

        private Task<ToolExecutionResult> Run(object arguments, bool requireLinux)
        {
            if (!OperatingSystem.IsWindows())
                Assert.Skip(WindowsOnlySkipReason);
            if (requireLinux && !ShellProbe.Wsl)
                Assert.Skip("WSL/Linux is unavailable or unresponsive; skipping the WSL-backed tool test");
            return _outer.Execute(WslTool.ToolName, JsonSerializer.Serialize(arguments, DshJson.Options));
        }

        [Fact]
        public async Task BlankDistro_IsRejected()
        {
        var result = await Run(new { command = "echo hi", distro = "  " }, requireLinux: false);

            Assert.True(result.IsError);
            Assert.Contains("distro", TextOf(result));
        }

        /** GitHub's Windows runners usually have no distribution, so either outcome must be readable and actionable. */
        [Fact]
        public async Task Command_ReturnsOutputOrAReadableWslDiagnostic()
        {
            var result = await Run(new
            {
                command = "echo \"$DSH_WSL_TEST-ok\"",
                timeoutMs = WslTimeoutMs,
                envs = new Dictionary<string, string> { ["DSH_WSL_TEST"] = "dropped" },
            });
            var text = TextOf(result);
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")))
            {
                Assert.True(result.IsError);
                Assert.Contains("WSL is not installed", text);
                return;
            }

            if (result is not ToolExecutionResult.Success { Value: var value }
                || !value.TryGetProperty("exitCode", out var exitCodeProperty))
            {
                Assert.Skip($"no usable WSL distribution on this machine: {text}");
                return;
            }
            Assert.DoesNotContain('\0', text);
            var exitCode = exitCodeProperty.GetInt32();
            if (exitCode == 0)
                Assert.Equal("-ok", text.Trim());
            else
                Assert.NotEqual("(no output)", text[..text.LastIndexOf("[exit code:", StringComparison.Ordinal)].Trim());
        }

        [Fact]
        public async Task Envs_AreForwardedIntoTheDistribution()
        {
            var result = await Run(new
            {
                command = "echo \"value=$WSL_TEST_VALUE\"",
                timeoutMs = WslTimeoutMs,
                envs = new Dictionary<string, string> { ["WSL_TEST_VALUE"] = "kept" },
            });
            if (result is not ToolExecutionResult.Success { Value: var value }
                || !value.TryGetProperty("exitCode", out var exitCode)
                || exitCode.GetInt32() != 0)
                Assert.Skip($"no usable WSL distribution on this machine: {TextOf(result)}");

            Assert.Equal("value=kept", TextOf(result).Trim());
        }
    }
}
