using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.CSharpRepl;
using Dsh.Csx;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class ScriptPermissionBoundaryTests
{
    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ExplicitScriptEntry_EnforcesWorkspacePermissions(bool read, bool write)
    {
        await RequireLinuxAsync();
        using var fixture = new Fixture(true, read, write, false, TestContext.Current.CancellationToken);
        using var child = await fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "if test -r host-input; then printf readable; fi; printf script > script-output"],
            Cwd = fixture.Workspace,
            Signal = TestContext.Current.CancellationToken,
        });
        await child.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        Assert.Equal(read, child.StdoutReader.ReadFrom(0).Text.Contains("readable", StringComparison.Ordinal));
        Assert.Equal(write, File.Exists(fixture.Output));
    }

    [Theory(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitScriptEntry_EnforcesNetworkPermission(bool network)
    {
        await RequireLinuxAsync();
        using var fixture = new Fixture(true, true, false, network, TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var child = await fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/bash", "-c", $"printf probe > /dev/tcp/127.0.0.1/{port}"],
            Cwd = fixture.Workspace,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.Equal(network, (await child.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode == 0);
        Assert.Equal(network, listener.Pending());
    }

    [Fact(Timeout = 60000)]
    public async Task WriteOnlyScriptGrant_RefusesBeforeExecutingInsteadOfGrantingRead()
    {
        using var fixture = new Fixture(true, false, true, false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = [Environment.ProcessPath!],
            Cwd = fixture.Workspace,
            Signal = TestContext.Current.CancellationToken,
        }));
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    public async Task OrdinaryProcessEntry_DoesNotInheritScriptDenials()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("This direct process control uses the installed Linux bash executable.");
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        using var child = await fixture.Processes.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "printf direct > script-output"],
            Cwd = fixture.Workspace,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.Equal(0, (await child.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal("direct", await File.ReadAllTextAsync(fixture.Output, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 120000)]
    [Trait("Category", "SandboxNative")]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task InstalledCSharpFrontends_AreScriptsAndNotDotnetExempt(bool repl, bool write)
    {
        await RequireLinuxAsync();
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
        Assert.Contains("*", reloaded.Safety.Whitelist);
    }


    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task ExplicitScriptEntry_DoesNotRequireAmbientModelState()
    {
        await RequireLinuxAsync();
        using var fixture = new Fixture(true, true, false, false, TestContext.Current.CancellationToken);
        using var handle = await fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "printf forbidden > script-output"],
            Cwd = fixture.Workspace,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.NotEqual(0, (await handle.Done.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken)).ExitCode);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task ScriptPrivateScratch_IsAHostGrantButGuestTmpDoesNotExposeHostTmp()
    {
        await RequireLinuxAsync();
        using var fixture = new Fixture(true, false, false, false, TestContext.Current.CancellationToken);
        var guestMarker = "/tmp/dsh-guest-only-" + Guid.NewGuid().ToString("N");
        Assert.False(File.Exists(guestMarker));
        string scratch;
        using (var handle = await fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "printf scratch > \"$TMPDIR/scratch-output\"; printf guest > \"$1\"; printf '%s\\n' \"$TMPDIR\"; IFS= read -r done", "scratch-probe", guestMarker],
            Cwd = fixture.Workspace,
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
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Processes.SpawnScriptAsync(new SubprocessSpawnSpec
        {
            Argv = ["/bin/sh", "-c", "exit 0"],
            Cwd = "/tmp",
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


    private static async Task RequireLinuxAsync()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("These script filesystem and network controls require the Linux bubblewrap backend.");
        await SandboxTestBackend.RequireAsync();
    }

    private static string TextOf(ToolExecutionResult result) => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private sealed class Fixture : IDisposable
    {
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
                    read: {{read.ToString().ToLowerInvariant()}}
                    write: {{write.ToString().ToLowerInvariant()}}
                    network: {{network.ToString().ToLowerInvariant()}}
                safety:
                  autoApprove: true
                  whitelist: ["*"]
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
            _ = new ScriptExecutionPolicy(Context, Home, HarnessSettings.Load(Home));
        }


        public Task<ToolExecutionResult> Execute(string name, object arguments, IAgent? agent = null)
            => Tools.Execute(new ToolExecutionInput
            {
                Name = name,
                CallId = ToolCallId.Create("script-permissions"),
                Arguments = JsonSerializer.SerializeToElement(arguments),
                Agent = agent,
                Signal = _signal,
            });


        public void Dispose()
        {
            Processes.Dispose();
            TempTree.Delete(Root);
        }
    }
}
