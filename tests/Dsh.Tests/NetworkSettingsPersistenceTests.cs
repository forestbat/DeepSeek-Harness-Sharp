using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.Runtime;
using Dsh.Fleet;
using System.Diagnostics;
using System.IO.Pipes;

namespace Dsh.Tests;

public sealed class NetworkSettingsPersistenceTests
{
    private const string FirstProxy = "http://127.0.0.1:7890";
    private const string SecondProxy = "socks5://127.0.0.1:1080";
    private const string Prefix = "# keep root\r\nglobal_default_model: null\r\nproviders: {}\r\nlogging: {file: false}\r\n";
    private const string Suffix = "\r\n# keep fleet\r\nfleet: {machines: [], deploy: {serverProxy: 'http://deploy:3128'}}\r\nplugins: {\"@deepseek-ai/dsh-memory\": false}\r\ncustom_root: [keep, me]\r\n";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Consumers_PreserveBlockAndFlowSiblingsCommentsAndCrlf(bool tui, bool flow)
    {
        var beforeTarget = flow
            ? "\"network\": {canary: {nested: [one, two]}, "
            : "\"network\": # network comment\r\n    canary: {nested: [one, two]} # sibling comment\r\n    ";
        var afterTarget = flow
            ? ", future_setting: 'keep: # value'} # network comment"
            : " # proxy comment\r\n    # keep sibling comment\r\n    future_setting: 'keep: # value'";
        await using var fixture = await Fixture.CreateAsync($"{Prefix}{beforeTarget}outbound_proxy: {{url: '{FirstProxy}'}}{afterTarget}{Suffix}");
        var oldPath = Path.Combine(fixture.Home.Root, "fleet", "machines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        byte[] oldBytes = [0, 13, 10, 255, 35, 72];
        await File.WriteAllBytesAsync(oldPath, oldBytes, TestContext.Current.CancellationToken);

        await fixture.SaveAsync(tui, SecondProxy);

        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.StartsWith($"{Prefix}{beforeTarget}", saved);
        Assert.EndsWith($"{afterTarget}{Suffix}", saved);
        Assert.Equal(saved.ReplaceLineEndings("\r\n"), saved);
        Assert.Equal(SecondProxy, HarnessSettings.Load(fixture.Home).Network?.OutboundProxy?.Url);
        Assert.Equal(new Uri(SecondProxy), fixture.Proxy.Address);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath, TestContext.Current.CancellationToken));

        await fixture.SaveAsync(!tui, null);

        var cleared = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains(flow ? "canary: {nested: [one, two]}" : "    canary: {nested: [one, two]} # sibling comment", cleared);
        Assert.Contains("future_setting: 'keep: # value'", cleared);
        Assert.Contains("# network comment", cleared);
        Assert.EndsWith(Suffix, cleared);
        Assert.DoesNotContain("outbound_proxy:", cleared);
        Assert.Null(HarnessSettings.Load(fixture.Home).Network?.OutboundProxy);
        Assert.Null(fixture.Proxy.Address);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "network: null\r\n")]
    [InlineData(true, "network: {}\r\n")]
    [InlineData(false, "network:\r\n    canary: keep\r\n")]
    [InlineData(true, "network: {canary: keep}\r\n")]
    public async Task Consumers_InsertIntoMissingOrEmptyMappings(bool tui, string network)
    {
        await using var fixture = await Fixture.CreateAsync($"{Prefix}{network}{Suffix}");

        await fixture.SaveAsync(tui, SecondProxy);

        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.StartsWith(Prefix, saved);
        Assert.Contains(Suffix, saved);
        if (network.Contains("canary", StringComparison.Ordinal))
            Assert.Contains("canary: keep", saved);
        Assert.Equal(SecondProxy, HarnessSettings.Load(fixture.Home).Network?.OutboundProxy?.Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumers_RereadCurrentFileBeforeSaving(bool tui)
    {
        await using var fixture = await Fixture.CreateAsync($"{Prefix}network: {{outbound_proxy: {{url: '{FirstProxy}'}}, canary: old}}{Suffix}");
        var current = $"{Prefix}network: {{outbound_proxy: {{url: '{FirstProxy}'}}, canary: updated, later: [1, 2]}}{Suffix}";
        await File.WriteAllTextAsync(fixture.Home.SettingsFile, current, TestContext.Current.CancellationToken);

        await fixture.SaveAsync(tui, SecondProxy);

        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("canary: updated, later: [1, 2]", saved);
        Assert.EndsWith(Suffix, saved);
        Assert.Equal(SecondProxy, HarnessSettings.Load(fixture.Home).Network?.OutboundProxy?.Url);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Consumers_ActualWriteFailureLeavesFileRouteAndNotificationsUnchanged(bool tui, bool clear)
    {
        var original = $"{Prefix}network: {{outbound_proxy: {{url: '{FirstProxy}', bypass: [keep.example]}}, canary: keep}}{Suffix}";
        await using var fixture = await Fixture.CreateAsync(original);
        var snapshot = Path.Combine(fixture.Home.Root, "preserved.yaml");
        File.Move(fixture.Home.SettingsFile, snapshot);
        Directory.CreateDirectory(fixture.Home.SettingsFile);
        var changes = 0;
        fixture.Facade.Changed += () => changes++;

        var error = await Record.ExceptionAsync(() => fixture.SaveAsync(tui, clear ? null : SecondProxy));

        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.True(Directory.Exists(fixture.Home.SettingsFile));
        Assert.Equal(original, await File.ReadAllTextAsync(snapshot, TestContext.Current.CancellationToken));
        Assert.Equal(new Uri(FirstProxy), fixture.Proxy.Address);
        Assert.Null(fixture.Proxy.ProxyFor(new Uri("https://keep.example/")));
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumers_InvalidUrlLeavesFileAndRouteUnchanged(bool tui)
    {
        var original = $"{Prefix}network: {{outbound_proxy: {{url: '{FirstProxy}'}}, canary: keep}}{Suffix}";
        await using var fixture = await Fixture.CreateAsync(original);

        if (tui)
            Assert.IsType<CommandResult.Error>(await fixture.CommandAsync("/proxy set gopher://invalid:70"));
        else
            Assert.Throws<ArgumentException>(() => fixture.Facade.UpdateOutboundProxy(new OutboundProxySettings { Url = "gopher://invalid:70" }));

        Assert.Equal(original, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Equal(new Uri(FirstProxy), fixture.Proxy.Address);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumers_PreserveRootFlowAndTrailingComma(bool tui)
    {
        var original = $"{{logging: {{file: false}}, network: {{canary: keep, }}, plugins: {{}}, custom: [one, two]}}\r\n";
        await using var fixture = await Fixture.CreateAsync(original);

        await fixture.SaveAsync(tui, SecondProxy);

        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.StartsWith("{logging: {file: false}, network: {canary: keep, ", saved);
        Assert.EndsWith("}, plugins: {}, custom: [one, two]}\r\n", saved);
        Assert.Equal(SecondProxy, fixture.Facade.Load().Network?.OutboundProxy?.Url);
        await fixture.SaveAsync(!tui, null);
        var cleared = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.StartsWith("{logging: {file: false}, network: {canary: keep", cleared);
        Assert.EndsWith("}, plugins: {}, custom: [one, two]}\r\n", cleared);
        Assert.Null(fixture.Facade.Load().Network?.OutboundProxy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumers_PreserveCommentsInsideReplacedBlock(bool tui)
    {
        var original = $"{Prefix}network:\r\n    canary: keep\r\n    outbound_proxy: # owned header\r\n        # owned comment\r\n        url: {FirstProxy} # url note\r\n        bypass: [] # bypass note\r\n{Suffix}";
        await using var fixture = await Fixture.CreateAsync(original);

        await fixture.SaveAsync(tui, SecondProxy);

        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("# owned header", saved);
        Assert.Contains("# owned comment", saved);
        Assert.Contains("# url note", saved);
        Assert.Contains("# bypass note", saved);
        Assert.StartsWith($"{Prefix}network:\r\n    canary: keep\r\n", saved);
        Assert.EndsWith(Suffix, saved);
        Assert.Equal(SecondProxy, fixture.Facade.Load().Network?.OutboundProxy?.Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumers_InvalidDocumentLeavesFileAndRouteUnchanged(bool tui)
    {
        await using var fixture = await Fixture.CreateAsync($"{Prefix}network: {{outbound_proxy: {{url: '{FirstProxy}'}}}}{Suffix}");
        const string invalid = "network: [unterminated";
        await File.WriteAllTextAsync(fixture.Home.SettingsFile, invalid, TestContext.Current.CancellationToken);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.SaveAsync(tui, SecondProxy)));

        Assert.Equal(invalid, await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
        Assert.Equal(new Uri(FirstProxy), fixture.Proxy.Address);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Consumers_ClearOnlyRootEntry_LeavesReloadableDocument(bool tui, bool flow)
    {
        var network = flow ? $"network: {{outbound_proxy: {{url: '{FirstProxy}'}}}}"
            : $"network:\n    outbound_proxy:\n        url: '{FirstProxy}'";
        await using var fixture = await Fixture.CreateAsync($"# root-only comment\n{network}\n");

        await fixture.SaveAsync(tui, null);

        Assert.Null(fixture.Facade.Load().Network?.OutboundProxy);
        Assert.Null(fixture.Proxy.Address);
        var saved = await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("# root-only comment", saved);
        Assert.DoesNotContain("outbound_proxy", saved);
        Assert.IsType<CommandResult.Success>(await fixture.CommandAsync("/proxy reload"));
    }

    [Fact]
    public async Task GuiSave_TuiReloadAndClear_PreserveProbeAndBypass()
    {
        await using var fixture = await Fixture.CreateAsync($"{Prefix}network: {{canary: keep}}{Suffix}");
        fixture.Facade.UpdateOutboundProxy(new OutboundProxySettings
        {
            Url = FirstProxy,
            Bypass = ["keep.example", "quoted\"host"],
            ProbeUrl = "https://example.com/probe",
        });

        Assert.IsType<CommandResult.Success>(await fixture.CommandAsync("/proxy reload"));
        Assert.IsType<CommandResult.Success>(await fixture.CommandAsync($"/proxy set {SecondProxy}"));
        var saved = fixture.Facade.Load().Network!.OutboundProxy!;
        Assert.Equal(["keep.example", "quoted\"host"], saved.Bypass);
        Assert.Equal("https://example.com/probe", saved.ProbeUrl);
        Assert.IsType<CommandResult.Success>(await fixture.CommandAsync("/proxy clear"));
        saved = fixture.Facade.Load().Network!.OutboundProxy!;
        Assert.Null(saved.Url);
        Assert.Equal(["keep.example", "quoted\"host"], saved.Bypass);
        Assert.Equal("https://example.com/probe", saved.ProbeUrl);
        Assert.Contains("canary: keep", await File.ReadAllTextAsync(fixture.Home.SettingsFile, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "machine: # 'quoted'")]
    [InlineData(true, "machine: # 'quoted'")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "中文 \"机器\"\nnext\\line")]
    [InlineData(true, "中文 \"机器\"\nnext\\line")]
    public async Task BorrowedSelection_TargetedSaveAndClear_PreserveBaselineAndEverySibling(bool flow, string selected)
    {
        var network = flow
            ? $"network: {{outbound_proxy: {{url: '{FirstProxy}', bypass: [keep.example]}}, future: {{nested: [one, two]}}}} # keep network"
            : $"network: # keep network\r\n  outbound_proxy: {{url: '{FirstProxy}', bypass: [keep.example]}}\r\n  future: {{nested: [one, two]}}";
        var root = TempTree.CreateSocketDirectory("b80p");
        try
        {
            var home = new HarnessHome(root);
            await File.WriteAllTextAsync(home.SettingsFile, $"{Prefix}{network}{Suffix}", TestContext.Current.CancellationToken);

            HarnessSettings.SaveBorrowedOutboundMachine(home, selected);

            var saved = await File.ReadAllTextAsync(home.SettingsFile, TestContext.Current.CancellationToken);
            Assert.Equal(selected, HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.StartsWith(Prefix, saved);
            Assert.EndsWith(Suffix, saved);
            Assert.Contains($"outbound_proxy: {{url: '{FirstProxy}', bypass: [keep.example]}}", saved);
            Assert.Contains("future: {nested: [one, two]}", saved);
            Assert.Contains("# keep network", saved);
            Assert.Equal(saved.ReplaceLineEndings("\r\n"), saved);

            HarnessSettings.SaveBorrowedOutboundMachine(home, null);

            var cleared = await File.ReadAllTextAsync(home.SettingsFile, TestContext.Current.CancellationToken);
            Assert.Null(HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.DoesNotContain("borrowed_outbound_machine", cleared);
            Assert.Contains($"outbound_proxy: {{url: '{FirstProxy}', bypass: [keep.example]}}", cleared);
            Assert.Contains("future: {nested: [one, two]}", cleared);
            Assert.EndsWith(Suffix, cleared);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void StaleLoadedSettings_SaveOnlyTheirEdits_AcrossRepeatedSaves()
    {
        var root = TempTree.CreateSocketDirectory("nps");
        try
        {
            var home = new HarnessHome(root);
            File.WriteAllText(home.SettingsFile, $"{Prefix}network: {{canary: keep}}{Suffix}");
            var settings = HarnessSettings.Load(home);
            HarnessSettings.SaveBorrowedOutboundMachine(home, "first-machine");
            settings.GlobalDefaultModel = "provider/first";
            settings.Save(home);
            Assert.Equal("first-machine", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);

            HarnessSettings.SaveBorrowedOutboundMachine(home, "second-machine");
            settings.CompactionModel = "provider/compact";
            settings.Save(home);
            var saved = HarnessSettings.Load(home);
            Assert.Equal("provider/first", saved.GlobalDefaultModel);
            Assert.Equal("provider/compact", saved.CompactionModel);
            Assert.Equal("second-machine", saved.Network?.BorrowedOutboundMachine);
            Assert.Contains("canary: keep", File.ReadAllText(home.SettingsFile));

            var destination = new HarnessHome(Path.Combine(root, "destination"));
            Directory.CreateDirectory(destination.Root);
            File.WriteAllText(destination.SettingsFile, $"{Prefix}network: {{borrowed_outbound_machine: destination-machine}}{Suffix}");
            settings.Save(destination);
            var copied = HarnessSettings.Load(destination);
            Assert.Equal("provider/first", copied.GlobalDefaultModel);
            Assert.Equal("provider/compact", copied.CompactionModel);
            Assert.Null(copied.Network?.BorrowedOutboundMachine);
            Assert.Equal("second-machine", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);

            new HarnessSettings { GlobalDefaultModel = "provider/full" }.Save(home);
            saved = HarnessSettings.Load(home);
            Assert.Equal("provider/full", saved.GlobalDefaultModel);
            Assert.Null(saved.Network);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Timeout = 90000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentProcesses_PreserveNetworkChildrenAndCompleteDocument_WithoutBlockingOtherHomes(bool independentHome)
    {
        var root = TempTree.CreateSocketDirectory("npc");
        var otherRoot = TempTree.CreateSocketDirectory("npi");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var cancellation = deadline.Token;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var children = new List<SettingsWriterProcess>();
        Task? publication = null;
        try
        {
            var home = new HarnessHome(root);
            var writerRoot = independentHome ? otherRoot : Path.Combine(root, ".");
            var writerHome = new HarnessHome(OperatingSystem.IsWindows() ? writerRoot.ToUpperInvariant() : writerRoot);
            var original = $"{Prefix}network: {{canary: {{nested: [one, two]}}}} # keep network{Suffix}";
            File.WriteAllText(home.SettingsFile, original);
            if (independentHome)
                File.WriteAllText(writerHome.SettingsFile, original);
            var settings = HarnessSettings.Load(home);
            settings.Plugins["transaction-owner"] = new PluginSetting { Parameters = new() { ["values"] = PausedValues() } };
            publication = Task.Run(() => settings.SavePlugins(home), CancellationToken.None);
            await entered.Task.WaitAsync(cancellation);

            foreach (var role in new[] { "borrowed", "proxy", "fleet-one", "fleet-two", "save" })
                children.Add(await SettingsWriterProcess.StartAsync(writerHome, role, cancellation));
            var completed = Task.WhenAll(children.Select(child => child.Saved));
            if (independentHome)
                await completed.WaitAsync(cancellation);
            else
            {
                var firstSaved = Task.WhenAny(children.Select(child => child.Saved));
                var exclusionWindow = Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                Assert.Same(exclusionWindow, await Task.WhenAny(firstSaved, exclusionWindow));
            }
            release.Set();
            await publication.WaitAsync(cancellation);
            await completed.WaitAsync(cancellation);
            foreach (var child in children)
                await child.AssertSuccessAsync(cancellation);

            var saved = HarnessSettings.Load(writerHome);
            Assert.Equal("selected-machine", saved.Network?.BorrowedOutboundMachine);
            Assert.Equal(SecondProxy, saved.Network?.OutboundProxy?.Url);
            Assert.Equal("provider/process", saved.GlobalDefaultModel);
            Assert.Equal(["fleet-one", "fleet-two"], new MachineStore(writerHome).Load().Machines.Select(machine => machine.Name).Order().ToArray());
            var text = File.ReadAllText(writerHome.SettingsFile);
            Assert.Contains("canary: {nested: [one, two]}", text);
            Assert.Contains("# keep network", text);
            Assert.Contains("custom_root: [keep, me]", text);
            Assert.Contains("# keep fleet", text);
            Assert.Equal(text.ReplaceLineEndings("\r\n"), text);
            Assert.True(HarnessSettings.Load(home).Plugins.ContainsKey("transaction-owner"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(writerHome.SettingsFile));
        }
        finally
        {
            release.Set();
            try
            {
                if (publication is not null)
                    await publication.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            finally
            {
                try
                {
                    await Task.WhenAll(children.Select(child => child.DisposeAsync().AsTask()));
                }
                finally
                {
                    TempTree.Delete(root);
                    TempTree.Delete(otherRoot);
                }
            }
        }

        IEnumerable<object?> PausedValues()
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(60), cancellation))
                throw new TimeoutException("The settings publication was not released.");
            yield return "preserved";
        }
    }

    [Fact(Explicit = true, Timeout = 90000)]
    public async Task SettingsWriterChild_UsesProductionSaves()
    {
        var pipeName = Environment.GetEnvironmentVariable("DSH_SETTINGS_TEST_PIPE");
        if (pipeName is null)
            Assert.Skip("This entry point is run only by an isolated settings writer process.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync("ready");
        var home = new HarnessHome(Environment.GetEnvironmentVariable(HarnessHome.HomeEnv)!);
        switch (Environment.GetEnvironmentVariable("DSH_SETTINGS_TEST_ROLE"))
        {
            case "borrowed":
                HarnessSettings.SaveBorrowedOutboundMachine(home, "selected-machine");
                break;
            case "proxy":
                using (var proxy = new OutboundProxy())
                    HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = SecondProxy }, proxy);
                break;
            case "fleet-one":
            case "fleet-two":
                new MachineStore(home).Upsert(new MachineSpec(Environment.GetEnvironmentVariable("DSH_SETTINGS_TEST_ROLE")!, MachineKinds.Ssh, "example.invalid"));
                break;
            case "save":
                var settings = HarnessSettings.Load(home);
                settings.GlobalDefaultModel = "provider/process";
                settings.Save(home);
                break;
            default:
                throw new InvalidOperationException("Unknown settings writer role.");
        }
        await writer.WriteLineAsync("saved");
    }

    private sealed class SettingsWriterProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        private SettingsWriterProcess(Process process, NamedPipeServerStream pipe)
        {
            _process = process;
            _pipe = pipe;
            _reader = new StreamReader(pipe);
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public Task<string?> Saved { get; private set; } = Task.FromResult<string?>(null);

        public static async Task<SettingsWriterProcess> StartAsync(HarnessHome home, string role, CancellationToken cancellation)
        {
            var name = OperatingSystem.IsWindows()
                ? $"dsh-settings-test-{Guid.NewGuid():N}"
                : Path.Combine(home.Root, role);
            if (!OperatingSystem.IsWindows())
                TempTree.AssertFitsMacOsSocketLimit(name);
            var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var executable = Path.Combine(AppContext.BaseDirectory, $"{typeof(NetworkSettingsPersistenceTests).Assembly.GetName().Name}{(OperatingSystem.IsWindows() ? ".exe" : "")}");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-method", $"{typeof(NetworkSettingsPersistenceTests).FullName}.{nameof(SettingsWriterChild_UsesProductionSaves)}", "-explicit", "on", "-parallelMode", "none", "-trait-", "Category=LlmSmoke" })
                start.ArgumentList.Add(argument);
            start.Environment[HarnessHome.HomeEnv] = home.Root;
            start.Environment["DSH_SETTINGS_TEST_PIPE"] = name;
            start.Environment["DSH_SETTINGS_TEST_ROLE"] = role;
            var child = new SettingsWriterProcess(Process.Start(start)!, pipe);
            try
            {
                await pipe.WaitForConnectionAsync(cancellation);
                Assert.Equal("ready", await child._reader.ReadLineAsync(cancellation));
                child.Saved = child._reader.ReadLineAsync(cancellation).AsTask();
                return child;
            }
            catch
            {
                await child.DisposeAsync();
                throw;
            }
        }

        public async Task AssertSuccessAsync(CancellationToken cancellation)
        {
            Assert.Equal("saved", await Saved.WaitAsync(cancellation));
            await _process.WaitForExitAsync(cancellation);
            Assert.Equal(0, _process.ExitCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                }
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(_stdout, _stderr).WaitAsync(deadline.Token);
            _reader.Dispose();
            await _pipe.DisposeAsync();
            _process.Dispose();
        }
    }

    private sealed class Fixture(HarnessApp app, string directory, IAgent agent) : IAsyncDisposable
    {
        public HarnessHome Home => app.Home;
        public OutboundProxy Proxy => OutboundProxy.FromContext(app.Ctx);
        public SettingsFacade Facade { get; } = new(app.Ctx, app.Home);

        public static async Task<Fixture> CreateAsync(string text)
        {
            var directory = TempTree.CreateSocketDirectory("np");
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(directory, "dsh.sock"));
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(directory, "run", "dsh-acp.sock"));
            var home = new HarnessHome(directory);
            await File.WriteAllTextAsync(home.SettingsFile, text, TestContext.Current.CancellationToken);
            var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: directory));
            var handle = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: directory));
            return new Fixture(app, directory, handle.Agent);
        }

        public async Task<CommandResult> CommandAsync(string line)
            => (await app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!.Execute(agent, line, TestContext.Current.CancellationToken))!.Result;

        public async Task SaveAsync(bool tui, string? url)
        {
            if (tui)
                Assert.IsType<CommandResult.Success>(await CommandAsync(url is null ? "/proxy clear" : $"/proxy set {url}"));
            else
                Facade.UpdateOutboundProxy(new OutboundProxySettings { Url = url });
        }

        public ValueTask DisposeAsync()
        {
            app.Dispose();
            TempTree.Delete(directory);
            return ValueTask.CompletedTask;
        }
    }
}
