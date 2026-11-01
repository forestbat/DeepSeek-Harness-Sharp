using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.ScriptHost;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class ExternalProcessBoundaryTests
{
    internal const string ProbeArgument = "--external-settings-probe";
    private const string Original = "global_default_model: unchanged\n";

    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("rename-parent")]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("child")]
    public async Task OpaqueBclMutation_IsDeniedOrUnsupported_ButNeverMutatesSettings(string operation)
    {
        using var fixture = ProtectedFixture();
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Start(operation, TestContext.Current.CancellationToken));
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace-output")));
            return;
        }
        using var handle = await fixture.Start(operation, TestContext.Current.CancellationToken);
        var outcome = await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("denied", handle.StdoutReader.ReadFrom(0).Text.Trim());
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Equal("workspace 中文", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "workspace-output"), TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task ActualToolCallback_CannotOmitModelExecutionToBypassTheBoundary()
    {
        using var fixture = ProtectedFixture();
        _ = new SystemPrompt(fixture.Context, new SystemPromptConfig());
        var tools = new ToolRuntime(fixture.Context);
        using var registration = tools.Register(new ToolDefinition
        {
            Name = "opaque_process_probe",
            Description = "Run a real component process without an explicit model marker.",
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject { ["type"] = "object" },
                (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = async (_, _) =>
            {
                using var child = await fixture.Processes.SpawnAsync(fixture.Spec("overwrite") with { ModelExecution = false });
                var outcome = await child.Done;
                return new JsonObject
                {
                    ["exitCode"] = outcome.ExitCode,
                    ["output"] = child.StdoutReader.ReadFrom(0).Text.Trim(),
                };
            },
        });
        var result = await tools.Execute(new ToolExecutionInput
        {
            Name = "opaque_process_probe",
            CallId = ToolCallId.Create("opaque-no-marker"),
            Arguments = JsonSerializer.SerializeToElement(new { }),
            Signal = TestContext.Current.CancellationToken,
        });
        if (OperatingSystem.IsWindows())
        {
            Assert.True(result.IsError);
            Assert.Contains("Windows direct writable-directory sandboxes",
                string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace-output")));
        }
        else
        {
            var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
            Assert.Equal(0, value.GetProperty("exitCode").GetInt32());
            Assert.Equal("denied", value.GetProperty("output").GetString());
        }
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task OutsideWorkspaceWrite_IsRestrictedOrUnsupported_WithoutMutatingTheTarget()
    {
        using var fixture = ProtectedFixture();
        var outside = TempTree.CreateDirectory("outside-external-settings");
        try
        {
            var target = Path.Combine(outside, "ordinary-file");
            await File.WriteAllTextAsync(target, "outside unchanged", TestContext.Current.CancellationToken);
            var spec = fixture.Spec("overwrite", TestContext.Current.CancellationToken) with { Argv = ProbeCommand("overwrite", target) };
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Processes.SpawnAsync(spec));
                Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace-output")));
            }
            else
            {
                using var handle = await fixture.Processes.SpawnAsync(spec);
                Assert.Equal(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
                Assert.Equal("denied", handle.StdoutReader.ReadFrom(0).Text.Trim());
                Assert.Equal("workspace 中文", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "workspace-output"), TestContext.Current.CancellationToken));
            }
            Assert.Equal("outside unchanged", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            TempTree.Delete(outside);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task RealShell_PreservesInputUnicodeEnvironmentExitAndSpill()
    {
        using var fixture = new Fixture();
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "$utf8=[Text.UTF8Encoding]::new($false); [Console]::InputEncoding=$utf8; [Console]::OutputEncoding=$utf8; $x=[Console]::ReadLine(); [Console]::Write($x + '|' + $env:BOUNDARY_VALUE); [Console]::Error.Write('real-stderr'); exit 7" }
            : new[] { "/bin/sh", "-c", "IFS= read -r x; printf '%s|%s' \"$x\" \"$BOUNDARY_VALUE\"; printf real-stderr >&2; exit 7" };
        using var handle = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = argv,
            Cwd = fixture.Root,
            ModelExecution = true,
            KeepStandardInputOpen = true,
            Env = new Dictionary<string, string?> { ["BOUNDARY_VALUE"] = "literal 中文 $(not-a-shell)" },
            Stdout = new SubprocessCollect(8, 4096),
            Signal = TestContext.Current.CancellationToken,
        });
        await handle.StandardInput.WriteLineAsync("stdin 中文".AsMemory(), TestContext.Current.CancellationToken);
        handle.StandardInput.Close();
        var outcome = await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        Assert.Equal(7, outcome.ExitCode);
        Assert.Equal("real-stderr", handle.StderrReader.ReadFrom(0).Text);
        var output = handle.StdoutReader.ReadFrom(0);
        Assert.True(output.Lossy);
        Assert.NotNull(output.SpillPath);
        Assert.Equal("stdin 中文|literal 中文 $(not-a-shell)", await File.ReadAllTextAsync(output.SpillPath, TestContext.Current.CancellationToken));
        File.Delete(output.SpillPath);
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task RealShell_CannotAppendToProtectedSettings_OrRefusesUnsupportedWritableCwd()
    {
        using var fixture = ProtectedFixture();
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; [IO.File]::AppendAllText($env:PROTECTED_FILE,'bad')" }
            : new[] { "/bin/sh", "-c", "printf bad >> \"$PROTECTED_FILE\"" };
        var spec = new SubprocessSpawnSpec
        {
            Argv = argv,
            Cwd = fixture.Root,
            ModelExecution = true,
            Env = new Dictionary<string, string?> { ["PROTECTED_FILE"] = fixture.SettingsPath },
            Signal = TestContext.Current.CancellationToken,
        };
        if (OperatingSystem.IsWindows())
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Processes.SpawnAsync(spec));
        else
        {
            using var handle = await fixture.Processes.SpawnAsync(spec);
            var outcome = await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
            Assert.NotEqual(0, outcome.ExitCode);
        }
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task CancelledOwnedProcess_ReleasesWorkspace_OrRefusesUnsupportedWritableCwdBeforeStarting()
    {
        using var fixture = ProtectedFixture();
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Start("wait", TestContext.Current.CancellationToken));
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace-output")));
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "after-refusal"), "usable", TestContext.Current.CancellationToken);
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handle = await fixture.Start("wait", cancellation.Token);
        using var process = Process.GetProcessById(handle.Pid);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(30));
        while (!handle.StdoutReader.ReadFrom(0).Text.Contains("ready", StringComparison.Ordinal))
        {
            if (handle.Done.IsCompleted)
                Assert.Fail("The real child exited before its cancellation boundary was exercised.");
            await Task.Delay(10, readiness.Token);
        }
        cancellation.Cancel();
        var outcome = await handle.Done.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal("SIGKILL", outcome.Signal);
        Assert.True(process.HasExited);
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "after-cancel"), "usable", TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task PtcDirectBcl_CannotBypassSettingsGate()
    {
        using var fixture = ProtectedFixture();
        var client = new PtcScriptHostClient(fixture.Processes, fixture.Root, [],
            (_, _) => throw new InvalidOperationException("This program must use real BCL IO, not tool dispatch."));
        var execution = client.RunAsync($$"""
            bool denied = false;
            try { System.IO.File.WriteAllText({{JsonSerializer.Serialize(fixture.SettingsPath)}}, "bad"); }
            catch (System.UnauthorizedAccessException) { denied = true; }
            catch (System.IO.IOException) { denied = true; }
            System.IO.File.WriteAllText("ptc-output", "workspace 中文");
            return new System.Text.Json.Nodes.JsonObject { ["denied"] = denied };
            """, 45000, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => execution);
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "ptc-output")));
            return;
        }
        var outcome = await execution;
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(outcome.Value!["denied"]!.GetValue<bool>());
        Assert.Equal("workspace 中文", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "ptc-output"), TestContext.Current.CancellationToken));
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 60000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultOff_ModelProgramRetainsDirectIoWithOrWithoutBoundary(bool installBoundary)
    {
        using var fixture = new Fixture(installBoundary: installBoundary);
        using var handle = await fixture.Start("overwrite", TestContext.Current.CancellationToken);
        Assert.Equal(19, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal("bad", await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultOff_PtcRetainsDirectBclIo()
    {
        using var fixture = new Fixture();
        var client = new PtcScriptHostClient(fixture.Processes, fixture.Root, [],
            (_, _) => throw new InvalidOperationException("This program uses real BCL IO."));
        var outcome = await client.RunAsync($$"""
            System.IO.File.WriteAllText({{JsonSerializer.Serialize(fixture.SettingsPath)}}, "ptc 中文");
            return new System.Text.Json.Nodes.JsonObject { ["written"] = System.IO.File.ReadAllText({{JsonSerializer.Serialize(fixture.SettingsPath)}}) };
            """, 45000, TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("ptc 中文", outcome.Value!["written"]!.GetValue<string>());
        Assert.Equal("ptc 中文", await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultOff_RealShellRetainsDirectSettingsIo()
    {
        using var fixture = new Fixture();
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "[IO.File]::AppendAllText($env:PROTECTED_FILE,'shell-write')" }
            : new[] { "/bin/sh", "-c", "printf shell-write >> \"$PROTECTED_FILE\"" };
        using var handle = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = argv,
            Cwd = fixture.Root,
            ModelExecution = true,
            Env = new Dictionary<string, string?> { ["PROTECTED_FILE"] = fixture.SettingsPath },
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.Equal(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(fixture.InitialSettings + "shell-write",
            await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "SandboxNative")]
    public async Task MacEnabled_OpaqueChildExecutionFailsBeforeMutation()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("The unsupported arbitrary-child ownership combination is macOS-specific.");
        using var fixture = new Fixture(sandboxEnabled: true);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => fixture.Start("overwrite", TestContext.Current.CancellationToken));
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace-output")));
    }

    internal static Fixture ProtectedFixture()
    {
        if (OperatingSystem.IsMacOS())
            Assert.Skip("Opaque child-spawning execution is explicitly unsupported when sandboxing is enabled on macOS.");
        return new Fixture(sandboxEnabled: true);
    }

    internal static int RunProbe(string[] arguments)
    {
        var at = Array.IndexOf(arguments, ProbeArgument);
        var operation = arguments[at + 1];
        var settings = arguments[at + 2];
        if (operation == "operator")
        {
            Console.WriteLine("operator");
            return 0;
        }
        if (operation == "wait")
        {
            Console.WriteLine("ready");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        if (operation == "child")
        {
            var command = ProbeCommand("overwrite", settings);
            var info = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEnd();
            child.StandardError.ReadToEnd();
            child.WaitForExit();
            Console.Write(output);
            return child.ExitCode;
        }
        try
        {
            switch (operation)
            {
                case "overwrite": File.WriteAllText(settings, "bad"); break;
                case "append": File.AppendAllText(settings, "bad"); break;
                case "replace":
                    File.WriteAllText("replacement", "bad");
                    File.Move("replacement", settings, overwrite: true);
                    break;
                case "rename-parent":
                    Directory.Move(Path.GetDirectoryName(settings)!, "renamed-config");
                    File.WriteAllText(Path.Combine("renamed-config", "settings.yaml"), "bad");
                    break;
                case "symlink":
                    File.CreateSymbolicLink("settings-alias", settings);
                    File.WriteAllText("settings-alias", "bad");
                    break;
                case "hardlink":
                    if (OperatingSystem.IsWindows() ? !CreateHardLink("settings-alias", settings, IntPtr.Zero) : Link(settings, "settings-alias") != 0)
                        throw new IOException("Hardlink creation was denied.");
                    File.WriteAllText("settings-alias", "bad");
                    break;
                default: throw new ArgumentException("Unknown process probe operation.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            File.WriteAllText("workspace-output", "workspace 中文");
            Console.WriteLine("denied");
            return 0;
        }
        Console.WriteLine("unprotected write succeeded");
        return 19;
    }

    private static string[] ProbeCommand(string operation, string settings)
    {
        var command = PtcScriptHostClient.ResolveHostCommand()!.Value;
        return [command.FileName, .. command.Arguments.Where(argument => argument != PtcScriptHost.HostArgument), ProbeArgument, operation, settings];
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string existing, IntPtr securityAttributes);

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = TempTree.CreateDirectory("external-settings");
        public Context Context { get; } = new();
        public string SettingsPath { get; }
        public string InitialSettings { get; }
        public SubprocessService Processes { get; }
        private readonly PluginHostAuthority? _authority;

        public Fixture(bool installBoundary = true, bool sandboxEnabled = false)
        {
            var home = new HarnessHome(Path.Combine(Root, "config"));
            Directory.CreateDirectory(home.Root);
            SettingsPath = home.SettingsFile;
            InitialSettings = sandboxEnabled ? Original + "sandbox:\n  enabled: true\n" : Original;
            File.WriteAllText(SettingsPath, InitialSettings);
            Processes = new SubprocessService(Context);
            if (installBoundary)
            {
                _ = new SettingsMutationBoundary(Context, home);
                if (sandboxEnabled)
                    _authority = new PluginHostAuthority(Context, home);
                _ = new ExternalProcessBoundary(Context, home, HarnessSettings.Load(home));
            }
        }

        public SubprocessSpawnSpec Spec(string operation, CancellationToken signal = default) => new()
        {
            Argv = ProbeCommand(operation, SettingsPath),
            Cwd = Root,
            ModelExecution = true,
            Signal = signal == default ? TestContext.Current.CancellationToken : signal,
        };

        public Task<SubprocessHandle> Start(string operation, CancellationToken signal = default)
            => Processes.SpawnAsync(Spec(operation, signal));

        public void Dispose()
        {
            Processes.Dispose();
            _authority?.Dispose();
            TempTree.Delete(Root);
        }
    }
}
