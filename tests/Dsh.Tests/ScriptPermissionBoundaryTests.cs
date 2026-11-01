using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.CSharpRepl;
using Dsh.Csx;
using Dsh.GitInspect;
using Dsh.Ide;
using Dsh.Ide.Catalog;
using Dsh.Ide.History;
using Dsh.Ide.Launch;
using Dsh.Ide.Surfaces;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.PtyTerminal;
using Dsh.Runtime;
using Dsh.ScriptHost;
using Dsh.Tools;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class ScriptPermissionBoundaryTests
{
    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task PtcBcl_WorkspacePermissionsAreIndependentAndEmptyGuestCwdStillComputes(bool read, bool write)
    {
        RequireLinux();
        using var fixture = new Fixture(true, read, write, false, TestContext.Current.CancellationToken);
        var result = await fixture.RunProgram(FileProgram());
        Assert.True(result.Ok, result.Message);
        var value = Assert.IsType<JsonObject>(result.Value);
        Assert.Equal(read, value["read"]!.GetValue<bool>());
        Assert.Equal(write || !read, value["write"]!.GetValue<bool>());
        Assert.Equal(fixture.Workspace, value["cwd"]!.GetValue<string>());
        Assert.Equal(42, value["computed"]!.GetValue<int>());
        Assert.Equal(write, File.Exists(fixture.Output));
        Assert.Equal("host input 中文", await File.ReadAllTextAsync(fixture.Input, TestContext.Current.CancellationToken));
        if (write)
            Assert.Equal("script output 中文", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PtcBcl_RealLoopbackConnectionFollowsOnlyScriptNetworkGrant(bool network)
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, false, network, TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await fixture.RunProgram(NetworkProgram(port));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(network, Assert.IsType<JsonObject>(result.Value)["connected"]!.GetValue<bool>());
        Assert.Equal(network, listener.Pending());
        if (network)
        {
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            Assert.True(accepted.Connected);
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SandboxOff_RejectsUnenforceableScriptGrantsBeforeExecuting(bool networkSetting)
    {
        using (var denied = new Fixture(false, false, false, networkSetting, TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => denied.RunProgram(FileProgram()));
            Assert.False(File.Exists(denied.Output));
            Assert.Equal("host input 中文", await File.ReadAllTextAsync(denied.Input, TestContext.Current.CancellationToken));
        }
        using var allowed = new Fixture(false, true, true, true, TestContext.Current.CancellationToken);
        var result = await allowed.RunProgram(FileProgram());
        Assert.True(result.Ok, result.Message);
        Assert.Equal("script output 中文", await File.ReadAllTextAsync(allowed.Output, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task WriteOnlyScriptGrant_RefusesBeforeExecutingInsteadOfGrantingRead()
    {
        using var fixture = new Fixture(true, false, true, false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.RunProgram("return 42;"));
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task UnsupportedGuestOnlyBackend_RefusesBeforeExecuting()
    {
        if (OperatingSystem.IsLinux())
            Assert.Skip("Linux supplies the guest-only tmpfs working-directory primitive.");
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<NotSupportedException>(() => fixture.RunProgram("return 42;"));
        Assert.False(File.Exists(fixture.Output));
    }

    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(SubprocessExecutionKind.Tool)]
    [InlineData(SubprocessExecutionKind.Mcp)]
    public async Task NonScriptOrigins_DoNotInheritScriptNamespaceDenials(SubprocessExecutionKind kind)
    {
        RequireLinux();
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var script = "printf helper > helper-output; exec /bin/bash -c 'echo helper >/dev/tcp/127.0.0.1/" + port + "'";
        using var handle = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/bash", "-c", script],
            Cwd = fixture.Workspace,
            ModelExecution = true,
            ExecutionKind = kind,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.Equal(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal("helper", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "helper-output"), TestContext.Current.CancellationToken));
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        Assert.True(accepted.Connected);
    }

    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealShellTool_UsesSafetyAndDoesNotInheritScriptGrants(bool write)
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, write, false, TestContext.Current.CancellationToken);
        using var registration = BashTool.Register(fixture.Context, new BashToolConfig { Cwd = fixture.Workspace });
        var result = await fixture.Execute("bash", new { command = "printf shell > script-output", workdir = fixture.Workspace });
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal(0, value.GetProperty("exitCode").GetInt32());
        Assert.Equal("shell", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task PersistentShell_NewSafetyProfileRetiresPreviouslyWritableProcess()
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, true, false, TestContext.Current.CancellationToken);
        using var agent = fixture.CreateAgent();
        using var registration = PersistentBashTool.Register(fixture.Context);
        var first = await fixture.Execute("bash_persistent", new { command = "boundary_previous_process=retire-me; printf first > script-output; printf policy-initial" }, agent);
        Assert.False(first.IsError, TextOf(first));
        Assert.Contains("policy-initial", TextOf(first));
        Assert.Equal("first", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
        HarnessSettings.Update(fixture.Home, settings =>
        {
            settings.Safety!.Blacklist["protect-output"] = new PermissionMatcher
                { Tool = ["bash_persistent"], Path = fixture.Output, Intent = "write" };
            return true;
        });
        var second = await fixture.Execute("bash_persistent", new { command = "printf second > script-output; printf 'previous=%s\\n' \"${boundary_previous_process-unset}\"; printf policy-current" }, agent);
        Assert.False(second.IsError, TextOf(second));
        Assert.Contains("policy-current", TextOf(second));
        Assert.Contains("previous=unset", TextOf(second));
        Assert.DoesNotContain("retire-me", TextOf(second));
        Assert.Equal("first", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 120000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task InstalledCSharpFrontends_AreScriptsAndNotDotnetExempt(bool repl, bool write)
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, write, true, TestContext.Current.CancellationToken);
        string executable;
        try
        {
            executable = await fixture.Processes.ResolveExecutable(repl ? "csharprepl" : "dotnet-script", signal: TestContext.Current.CancellationToken);
        }
        catch (FileNotFoundException)
        {
            Assert.Skip("The optional real C# frontend is not installed.");
            return;
        }
        var program = """
            System.Console.WriteLine("frontend-started-中文");
            try
            {
                System.IO.File.WriteAllText("script-output", "frontend output 中文");
                System.Console.WriteLine("frontend-write=True");
            }
            catch (System.Exception error) when (error is System.UnauthorizedAccessException || error is System.IO.IOException)
            {
                System.Console.WriteLine($"frontend-denied={error.GetType().Name}");
                throw;
            }
            """;
        var script = Path.Combine(fixture.Workspace, "frontend.csx");
        await File.WriteAllTextAsync(script, program, TestContext.Current.CancellationToken);
        using var registration = repl
            ? ToolRunCSharpRepl.Register(fixture.Context, new CSharpReplConfig { Executable = executable })
            : ToolRunCsx.Register(fixture.Context, fixture.Workspace, new CsxPluginConfig { Executable = executable });
        var result = repl
            ? await fixture.Execute("run_csharp_repl", new { code = program, description = "Exercise real script permission boundary", workdir = fixture.Workspace })
            : await fixture.Execute("run_csx", new { script, workdir = fixture.Workspace });
        var diagnostic = System.Text.RegularExpressions.Regex.Replace(TextOf(result),
            """(?i)(?:[a-z][a-z0-9+.-]*://[^\s"'<>]+|(?:[a-z]:[\\/]|/)[^\s"'<>]*|[\w.+-]+@[\w.-]+)""", "<redacted>");
        diagnostic = diagnostic[..Math.Min(diagnostic.Length, 8192)];
        Assert.True(result.IsError == !write, diagnostic);
        if (write)
        {
            var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
            var stdout = repl ? value.GetProperty("stdout").GetProperty("text").GetString() : value.GetProperty("stdout").GetString();
            Assert.True(stdout?.Contains("frontend-started-中文", StringComparison.Ordinal) == true, diagnostic);
            Assert.True(stdout?.Contains("frontend-write=True", StringComparison.Ordinal) == true, diagnostic);
            Assert.Equal("frontend output 中文", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
        }
        else
        {
            var error = TextOf(result);
            Assert.True(error.Contains("frontend-started-中文", StringComparison.Ordinal), diagnostic);
            Assert.True(error.Contains("frontend-denied=", StringComparison.Ordinal), diagnostic);
            Assert.True(!error.Contains("frontend-write=True", StringComparison.Ordinal), diagnostic);
        }
        Assert.Equal(write, File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task ProtectedModelTerminalOpenAndSend_RefuseUnprotectedNativeBackend()
    {
        using var fixture = new Fixture(true, true, true, true, TestContext.Current.CancellationToken);
        using var agent = fixture.CreateAgent();
        var terminals = new TerminalSessionService(fixture.Context);
        using var backend = PtyTerminalPlugin.Register(fixture.Context);
        using var tools = TerminalTools.Register(fixture.Context);
        var opened = await fixture.Execute("terminal_open", new { type = "shell" }, agent);
        Assert.True(opened.IsError);
        Assert.Empty(terminals.List(agent));
        var sent = await fixture.Execute("terminal_send", new { sessionId = "unprotected-existing", text = "printf unsafe > script-output" }, agent);
        Assert.True(sent.IsError);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task ExistingOffTerminal_CannotAcceptModelInputAfterLiveToggle()
    {
        const string originalContent = "off-mode-original";
        using var fixture = new Fixture(false, false, false, false, TestContext.Current.CancellationToken);
        using var agent = fixture.CreateAgent();
        var terminals = new TerminalSessionService(fixture.Context);
        using var backend = PtyTerminalPlugin.Register(fixture.Context, new PtyTerminalConfig
        {
            Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Args = OperatingSystem.IsWindows() ? ["/Q"] : ["--noprofile", "--norc", "-i"],
        });
        var opened = await terminals.Spawn(agent, new TerminalSpawnRequest("shell", null, fixture.Workspace), TestContext.Current.CancellationToken);
        try
        {
            var initial = terminals.StartSend(agent, opened.SessionId, new TerminalSendRequest($"echo {originalContent}>script-output", true, TestContext.Current.CancellationToken));
            await initial.Done.WaitAsync(TimeSpan.FromSeconds(35), TestContext.Current.CancellationToken);
            Assert.Equal(originalContent, (await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken)).Trim());
            fixture.SetEnabled(true);
            Assert.Throws<InvalidOperationException>(() => terminals.StartSend(agent, opened.SessionId, new TerminalSendRequest("echo unsafe>script-output", true)));
            Assert.Equal(originalContent, (await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            await terminals.Kill(agent, opened.SessionId);
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSandboxToggle_RequiresRestartBeforeOpaqueLaunch(bool startupEnabled)
    {
        using var fixture = new Fixture(startupEnabled, true, true, true, TestContext.Current.CancellationToken);
        fixture.SetEnabled(!startupEnabled);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunProgram("return 42;"));
        Assert.Contains("restarting", error.Message);
    }

    [Fact(Timeout = 60000)]
    public void ScriptSettings_RoundTripWithoutChangingPluginPermissionsOrSafety()
    {
        using var fixture = new Fixture(false, false, true, false, TestContext.Current.CancellationToken);
        var settings = HarnessSettings.Load(fixture.Home);
        Assert.False(settings.Scripts!.Permissions!.Read);
        Assert.True(settings.Scripts.Permissions.Write);
        Assert.False(settings.Scripts.Permissions.Network);
        Assert.True(settings.Plugins["permission-probe"].Permissions!.Network);
        settings.Scripts.Permissions.Write = false;
        settings.Save(fixture.Home);
        var reloaded = HarnessSettings.Load(fixture.Home);
        Assert.False(reloaded.Scripts!.Permissions!.Write);
        Assert.True(reloaded.Plugins["permission-probe"].Permissions!.Read);
        Assert.True(reloaded.Plugins["permission-probe"].Permissions!.Write);
        Assert.True(reloaded.Safety!.AutoApprove);
        Assert.Contains("allow-all", reloaded.Safety.Whitelist.Keys);
    }

    [Fact(Timeout = 60000)]
    public async Task DirectGitConsumers_RefuseProtectedScopeAndManualVersionRemainsAvailable()
    {
        using var fixture = new Fixture(false, true, true, true, TestContext.Current.CancellationToken);
        var before = await GitCommand.RunAsync(Environment.CurrentDirectory, ["version"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, before.ExitCode);
        using (new GitExecutionScope())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => GitCommand.RunAsync(Environment.CurrentDirectory, ["version"], cancellationToken: TestContext.Current.CancellationToken));
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<NotSupportedException>(() => GitCommand.CopyOutputAsync(Environment.CurrentDirectory, ["version"], output, TestContext.Current.CancellationToken));
            Assert.Throws<NotSupportedException>(() => GitHeadSnapshot.Read(fixture.Workspace, [fixture.Input]));
        }
        Assert.Equal(0, (await GitCommand.RunAsync(Environment.CurrentDirectory, ["version"], cancellationToken: TestContext.Current.CancellationToken)).ExitCode);
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task PluginDenials_DoNotSupplyScriptPermissions()
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, true, true, TestContext.Current.CancellationToken);
        HarnessSettings.UpdatePlugin(fixture.Home, "permission-probe", setting => (setting ?? new PluginSetting()) with
        {
            Permissions = new PluginPermissions { Read = false, Write = false, Network = false },
        });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var program = $$"""
            var input = System.IO.File.ReadAllText("host-input");
            System.IO.File.WriteAllText("script-output", "independent");
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, {{port}}).WaitAsync(System.TimeSpan.FromSeconds(3));
            return new System.Text.Json.Nodes.JsonObject { ["read"] = input == "host input 中文", ["connected"] = client.Connected };
            """;
        var result = await fixture.RunProgram(program);
        Assert.True(result.Ok, result.Message);
        var value = Assert.IsType<JsonObject>(result.Value);
        Assert.True(value["read"]!.GetValue<bool>());
        Assert.True(value["connected"]!.GetValue<bool>());
        Assert.Equal("independent", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        Assert.True(accepted.Connected);
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task ScriptOrigin_CannotOmitModelMarkerToEscapeConfinement()
    {
        RequireLinux();
        using var fixture = new Fixture(true, true, false, false, TestContext.Current.CancellationToken);
        using var handle = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "printf forbidden > script-output"],
            Cwd = fixture.Workspace,
            ExecutionKind = SubprocessExecutionKind.Script,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.NotEqual(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task ScriptPrivateScratch_IsAHostGrantButGuestTmpDoesNotExposeHostTmp()
    {
        RequireLinux();
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        var guestMarker = "/tmp/dsh-guest-only-" + Guid.NewGuid().ToString("N");
        Assert.False(File.Exists(guestMarker));
        string scratch;
        using (var handle = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "printf scratch > \"$TMPDIR/scratch-output\"; printf guest > \"$1\"; printf '%s\\n' \"$TMPDIR\"; IFS= read -r done", "scratch-probe", guestMarker],
            Cwd = fixture.Workspace,
            ExecutionKind = SubprocessExecutionKind.Script,
            KeepStandardInputOpen = true,
            Signal = TestContext.Current.CancellationToken,
        }))
        {
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(30));
            while (!handle.StdoutReader.ReadFrom(0).Text.Contains('\n'))
            {
                if (handle.Done.IsCompleted)
                {
                    var outcome = await handle.Done;
                    var stderr = System.Text.RegularExpressions.Regex.Replace(
                        handle.StderrReader.ReadFrom(0).Text, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
                    Assert.Fail($"The scratch probe exited before readiness: exit={outcome.ExitCode}, signal={outcome.Signal}, stderr={stderr[..Math.Min(stderr.Length, 2048)]}");
                }
                await Task.Delay(10, readiness.Token);
            }
            scratch = handle.StdoutReader.ReadFrom(0).Text.Trim();
            Assert.Equal("scratch", await File.ReadAllTextAsync(Path.Combine(scratch, "scratch-output"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(guestMarker));
            await handle.StandardInput.WriteLineAsync("done");
            await handle.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ExitCode);
        }
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(guestMarker));
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task GuestCwd_CannotReplaceThePrivateTmpRoot()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Linux-only guest working-directory validation.");
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "exit 0"],
            Cwd = "/tmp",
            ExecutionKind = SubprocessExecutionKind.Script,
            Signal = TestContext.Current.CancellationToken,
        }));
    }

    [Theory(Timeout = 60000)]
    [InlineData("scripts: false\n")]
    [InlineData("scripts:\n  permission: {write: false}\n")]
    [InlineData("scripts:\n  permissions: []\n")]
    [InlineData("scripts:\n  permissions: {wirte: false}\n")]
    [InlineData("scripts:\n  permissions: {write: deny}\n")]
    public void InvalidScriptConfiguration_IsRejectedByActualSettingsLoader(string document)
    {
        using var fixture = new Fixture(false, true, true, true, TestContext.Current.CancellationToken);
        File.WriteAllText(fixture.Home.SettingsFile, document);
        Assert.Throws<RuntimeException>(() => HarnessSettings.Load(fixture.Home));
    }

    [Fact(Timeout = 60000)]
    public async Task ModelIdeSurfaceAndAutomaticLauncher_RefuseUnconfinedProcessesButKeepPureStatus()
    {
        using var fixture = new Fixture(true, true, true, true, TestContext.Current.CancellationToken);
        using var agent = fixture.CreateAgent();
        var catalog = new EditorCatalog([], new EditorDetector(new Dictionary<string, string[]>(), [], []));
        var editors = new EditorLauncher(catalog);
        using var registration = IdeTool.Register(fixture.Context, editors);
        Assert.True((await fixture.Execute("ide", new { action = "list" }, agent)).IsError);
        Assert.False((await fixture.Execute("ide", new { action = "status" }, agent)).IsError);
        using var automatic = fixture.Tools.Register(new ToolDefinition
        {
            Name = "automatic_editor_probe",
            Description = "Exercise the actual automatic editor launch consumer.",
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject { ["type"] = "object" }, (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = async (_, exec) => await editors.LaunchAsync(new EditorRequest
            {
                WorkspaceRoot = fixture.Workspace,
                Path = fixture.Input,
                Action = EditorAction.OpenFile,
            }, exec.Signal),
        });
        var result = await fixture.Execute("automatic_editor_probe", new { }, agent);
        Assert.True(result.IsError);
        Assert.Contains("OS-confined", TextOf(result));
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task PersistentShell_IndependentScriptGrantChangesDoNotRetireCommandState()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("The positive legacy control uses the installed Linux bash executable.");
        using var fixture = new Fixture(false, true, true, true, TestContext.Current.CancellationToken);
        using var agent = fixture.CreateAgent();
        using var registration = PersistentBashTool.Register(fixture.Context);
        Assert.False((await fixture.Execute("bash_persistent", new { command = "boundary_legacy_state=retained" }, agent)).IsError);
        fixture.SetWrite(false);
        var result = await fixture.Execute("bash_persistent", new { command = "printf '%s' \"$boundary_legacy_state\"" }, agent);
        Assert.False(result.IsError);
        Assert.Contains("retained", TextOf(result));
    }

    private static string FileProgram() => """
        bool read = false;
        bool write = false;
        try { read = System.IO.File.ReadAllText("host-input") == "host input 中文"; }
        catch (System.UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
        try { System.IO.File.WriteAllText("script-output", "script output 中文"); write = true; }
        catch (System.UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
        return new System.Text.Json.Nodes.JsonObject {
            ["read"] = read, ["write"] = write, ["cwd"] = System.IO.Directory.GetCurrentDirectory(), ["computed"] = 6 * 7
        };
        """;

    private static string NetworkProgram(int port) => $$"""
        bool connected = false;
        try {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, {{port}}).WaitAsync(System.TimeSpan.FromSeconds(3));
            connected = client.Connected;
        }
        catch (System.Net.Sockets.SocketException) { }
        catch (System.UnauthorizedAccessException) { }
        catch (System.TimeoutException) { }
        return new System.Text.Json.Nodes.JsonObject { ["connected"] = connected };
        """;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("These positive controls require the maintained Linux bubblewrap/cgroup/seccomp backend; unsupported backends are covered by refusal tests.");
    }

    private static string TextOf(ToolExecutionResult result) => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private sealed class Fixture : IDisposable
    {
        private readonly PluginHostAuthority? _authority;
        private readonly AgentRegistry _agents;
        private readonly CancellationToken _signal;
        public string Root { get; } = TempTree.CreateDirectory("script-permissions");
        public string Workspace { get; }
        public string Input => Path.Combine(Workspace, "host-input");
        public string Output => Path.Combine(Workspace, "script-output");
        public HarnessHome Home { get; }
        public Context Context { get; } = new();
        public SubprocessService Processes { get; }
        public ToolRuntime Tools { get; }

        public Fixture(bool enabled, bool read, bool write, bool network, CancellationToken signal)
        {
            _signal = signal;
            Workspace = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(Workspace);
            Home = new HarnessHome(Path.Combine(Root, "config"));
            Directory.CreateDirectory(Home.Root);
            File.WriteAllText(Input, "host input 中文");
            File.WriteAllText(Home.SettingsFile, $$"""
                sandbox:
                  enabled: {{enabled.ToString().ToLowerInvariant()}}
                scripts:
                  permissions:
                    autoApprove: true
                    read: {{read.ToString().ToLowerInvariant()}}
                    write: {{write.ToString().ToLowerInvariant()}}
                    network: {{network.ToString().ToLowerInvariant()}}
                safety:
                  autoApprove: true
                  whitelist: { allow-all: {} }
                plugins:
                  permission-probe:
                    permissions:
                      read: true
                      write: true
                      network: true
                """);
            _ = new SystemPrompt(Context, new SystemPromptConfig());
            Processes = new SubprocessService(Context);
            Tools = new ToolRuntime(Context);
            _agents = new AgentRegistry(Context);
            _ = new SettingsMutationBoundary(Context, Home);
            if (enabled)
                _authority = new PluginHostAuthority(Context, Home);
            _ = new ExternalProcessBoundary(Context, Home, HarnessSettings.Load(Home));
        }

        public Task<PtcRunOutcome> RunProgram(string program)
            => new PtcScriptHostClient(Processes, Workspace, [], (_, _) => throw new InvalidOperationException("The test must execute real BCL effects, not tool dispatch."))
                .RunAsync(program, 45000, _signal);

        public Task<ToolExecutionResult> Execute(string name, object arguments, IAgent? agent = null)
            => Tools.Execute(new ToolExecutionInput
            {
                Name = name,
                CallId = ToolCallId.Create("script-permissions"),
                Arguments = JsonSerializer.SerializeToElement(arguments),
                Agent = agent,
                Signal = _signal,
            });

        public AgentLoopAgent CreateAgent()
        {
            var id = SessionId.Create("script-permission-owner-" + Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Workspace,
                IsSeeded = false,
            });
            var agent = new AgentLoopAgent(Context, id, new AgentOptions(), session, static _ => 0);
            _agents.Register(agent);
            return agent;
        }

        public void SetWrite(bool write)
            => HarnessSettings.Update(Home, settings => { settings.Scripts!.Permissions!.Write = write; return true; });

        public void SetEnabled(bool enabled)
            => HarnessSettings.Update(Home, settings => { settings.Sandbox!.Enabled = enabled; return true; });

        public void Dispose()
        {
            Processes.Dispose();
            _authority?.Dispose();
            TempTree.Delete(Root);
        }
    }
}
