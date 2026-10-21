using System.Text.Json;
using Dsh.Boot;
using Dsh.Fleet;

namespace Dsh.Tests;

/** 机器清单: GUI、CLI 与 WebBridge 只读写 settings.yaml 的 fleet 段。 */
public sealed class MachineStoreTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("fleet-store");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Machines_RoundTrip_With_All_Fields_In_The_OwnerOnly_Settings_File()
    {
        var store = new MachineStore(new HarnessHome(_root));
        MachineSpec[] machines =
        [
            MachineSpec.Ssh("build", "build-box.internal", 22, "builder", SshAuth.Password) with { Proxy = "nc -x 127.0.0.1:1080 %h %p", RemotePath = "~/work", Password = "null" },
            MachineSpec.Ssh("gpu", "gpu-box.internal", 2222, "dev", SshAuth.Key) with { KeyPath = "C:/keys/id_ed25519", HarnessHome = "~/.dsh-gpu", SshOptions = ["UserKnownHostsFile=/tmp/kh"] },
            MachineSpec.Local("/srv/second-home", "second"),
            MachineSpec.Ssh("home", "", 22, "dev", SshAuth.Key) with
            {
                Kind = MachineKinds.Iroh,
                EndpointId = new string('a', MachineSpec.IrohEndpointIdLength),
                RelayUrls = ["https://relay.example.org/"],
                KeyPath = "/keys/home",
            },
        ];
        store.Save(new FleetConfig(machines, new DeploySettings { AutoDeploy = false, Via = DownloadVia.Client, ReleaseVersion = "v0.2.0" }));

        var reloaded = store.Load();
        Assert.Equal(machines.Length, reloaded.Machines.Count);
        Assert.Equal(machines[0], reloaded.Machines[0]);
        Assert.Equal(machines[1] with { SshOptions = null }, reloaded.Machines[1] with { SshOptions = null });
        Assert.Equal(["UserKnownHostsFile=/tmp/kh"], reloaded.Machines[1].SshOptions);
        Assert.Equal(machines[2], reloaded.Machines[2]);
        Assert.Equal(machines[3] with { RelayUrls = null }, reloaded.Machines[3] with { RelayUrls = null });
        Assert.Equal(machines[3].RelayUrls, reloaded.Machines[3].RelayUrls);
        Assert.False(reloaded.Deploy.AutoDeploy);
        Assert.Equal(DownloadVia.Client, reloaded.Deploy.Via);
        Assert.Equal([MachineSpec.LocalName, "build", "gpu", "second", "home"], reloaded.AllMachines().Select(machine => machine.Name));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public void Upsert_Replaces_By_Name_And_Remove_Reports_Missing_Machines()
    {
        var store = new MachineStore(new HarnessHome(_root));
        store.Upsert(MachineSpec.Ssh("box", "a.internal", 22, "me", SshAuth.Agent));
        store.Upsert(MachineSpec.Ssh("box", "b.internal", 22, "me", SshAuth.Agent));

        Assert.Equal("b.internal", Assert.Single(store.Load().Machines).Host);
        Assert.True(store.Remove("box"));
        Assert.False(store.Remove("box"));
        Assert.Empty(store.Load().Machines);
    }

    [Theory]
    [InlineData(MachineSpec.LocalName)]
    [InlineData("a/b")]
    [InlineData("")]
    public void Reserved_Or_Invalid_Names_Are_Rejected(string name)
    {
        var store = new MachineStore(new HarnessHome(_root));
        Assert.Throws<InvalidOperationException>(() => store.Upsert(MachineSpec.Ssh(name, "host", 22, "me", SshAuth.Agent)));
    }

    [Fact]
    public void Duplicate_Names_Are_Rejected()
    {
        var store = new MachineStore(new HarnessHome(_root));
        var machine = MachineSpec.Ssh("box", "host", 22, "me", SshAuth.Agent);
        Assert.Throws<InvalidOperationException>(() => store.Save(FleetConfig.Empty with { Machines = [machine, machine] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loading_UsesOnlyFleetSettings_AndLeavesUnrelatedFilesAndPluginParametersUntouched(bool configured)
    {
        var home = new HarnessHome(_root);
        var machine = MachineSpec.Ssh("keep", "keep.internal", 22, "dev", SshAuth.Agent);
        var store = new MachineStore(home);
        File.WriteAllText(home.SettingsFile, """

            plugins:
              "@deepseek-ai/dsh-gui":
                remoteWorkspaces: [{ name: ignored, host: ignored.internal }]
                remoteAutoDeploy: false
                remoteDownloadProxy: http://ignored-proxy:3128
            """);
        if (configured)
            store.Save(new FleetConfig([machine], new DeploySettings { ServerProxy = "http://remote-proxy:3128" }));
        var original = File.ReadAllText(home.SettingsFile);
        var unrelatedFile = Path.Combine(_root, "fleet", "machines.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedFile)!);
        const string unrelatedContents = "{not a valid configuration";
        File.WriteAllText(unrelatedFile, unrelatedContents);

        var config = store.Load();

        if (configured)
            Assert.Equal(machine, Assert.Single(config.Machines));
        else
            Assert.Empty(config.Machines);
        Assert.True(config.Deploy.AutoDeploy);
        Assert.Equal(configured ? "http://remote-proxy:3128" : null, config.Deploy.ServerProxy);
        Assert.Null(HarnessSettings.Load(home).Network?.OutboundProxy);
        Assert.Equal(original, File.ReadAllText(home.SettingsFile));
        Assert.Equal(unrelatedContents, File.ReadAllText(unrelatedFile));
    }

    /** 只改写 fleet 段: 注释、其它段与 CRLF 换行原样保留; 删掉最后一台机器后文件回到原样。 */
    [Fact]
    public void Saving_Rewrites_Only_The_Fleet_Section_And_Keeps_Comments_And_Crlf()
    {
        var home = new HarnessHome(_root);
        Directory.CreateDirectory(_root);
        var original = string.Join("\r\n",
            "# my settings",
            "global_default_model: deepseek-official/deepseek-v4-flash  # keep this comment",
            "",
            "# providers I use",
            "providers:",
            "  deepseek-official:",
            "    type: deepseek",
            "plugins:",
            "  \"@deepseek-ai/dsh-memory\": false",
            "");
        File.WriteAllText(home.SettingsFile, original);
        var store = new MachineStore(home);

        store.Upsert(MachineSpec.Ssh("box", "box.internal", 22, "me", SshAuth.Agent));
        var saved = File.ReadAllText(home.SettingsFile);

        Assert.StartsWith(original.TrimEnd(), saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", saved.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("fleet:", saved, StringComparison.Ordinal);
        Assert.Equal("box.internal", Assert.Single(store.Load().Machines).Host);

        Assert.True(store.Remove("box"));
        Assert.Equal(original, File.ReadAllText(home.SettingsFile));
    }

    [Fact]
    public void Loading_Without_Any_Configuration_Creates_Nothing()
    {
        var home = new HarnessHome(_root);
        Assert.Empty(new MachineStore(home).Load().Machines);
        Assert.False(File.Exists(home.SettingsFile));
    }

    [Fact]
    public void OutboundTunnel_RoundTrip_And_Clear_Keep_BaseProxy_Deploy_And_Unrelated_Settings()
    {
        var home = new HarnessHome(_root);
        const string unrelated = "# retained settings\ncustom_section:\n  retained: value\nnetwork:\n  outbound_proxy:\n    url: http://base.example:3128\n";
        File.WriteAllText(home.SettingsFile, unrelated);
        var store = new MachineStore(home);
        var machine = MachineSpec.Ssh("target", "ssh.example", 22, "dev", SshAuth.Agent) with
        {
            Proxy = "existing SSH ProxyCommand",
            OutboundTunnel = new OutboundTunnelSpec("proxyCommand", "https://tunnel-user:tunnel-secret@proxy.example:8443"),
        };
        var sibling = MachineSpec.Ssh("sibling", "other.example", 22, "dev", SshAuth.Agent);
        var deploy = new DeploySettings { ServerProxy = "http://deploy.example:3128", AutoDeploy = false };
        store.Save(new FleetConfig([machine, sibling], deploy));

        var loaded = store.Load();
        Assert.Equal(machine, loaded.Machines[0]);
        Assert.Equal(sibling, loaded.Machines[1]);
        Assert.Equal(deploy, loaded.Deploy);
        Assert.StartsWith(unrelated, File.ReadAllText(home.SettingsFile), StringComparison.Ordinal);
        Assert.Equal("http://base.example:3128", HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(home.SettingsFile));

        store.Upsert(machine with { OutboundTunnel = null });
        Assert.Null(store.Load().Find("target")!.OutboundTunnel);
        Assert.Equal(sibling, store.Load().Find("sibling"));
        Assert.Equal(deploy, store.Load().Deploy);
        Assert.DoesNotContain("outboundTunnel:", File.ReadAllText(home.SettingsFile));
        Assert.StartsWith(unrelated, File.ReadAllText(home.SettingsFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://proxy.example")]
    [InlineData("https://proxy.example")]
    [InlineData("http://tunnel-user:tunnel-secret@proxy.example:3128/")]
    [InlineData("https://tunnel-user:tunnel-secret@proxy.example:8443")]
    [InlineData("socks4://proxy.example:1080")]
    [InlineData("socks4a://proxy.example:1080")]
    [InlineData("socks5://tunnel-user:tunnel-secret@[::1]:1080")]
    public void OutboundTunnel_Validation_Preserves_Logical_Endpoint_And_Authentication(string proxyUrl)
    {
        var machine = MachineSpec.Ssh("target", "ssh.example", 22, "dev", SshAuth.Agent) with
        {
            OutboundTunnel = new OutboundTunnelSpec("proxyCommand", proxyUrl),
        };
        var validated = MachineStore.ValidateOutboundTunnel(machine);
        Assert.NotNull(validated);
        Assert.Equal(proxyUrl, validated.OriginalString);
        var store = new MachineStore(new HarnessHome(_root));
        store.Upsert(machine);
        Assert.Equal(machine, Assert.Single(store.Load().Machines));
    }

    [Theory]
    [InlineData(null, "http://tunnel-user:tunnel-secret@proxy.example:3128", null)]
    [InlineData("", "http://tunnel-user:tunnel-secret@proxy.example:3128", null)]
    [InlineData("proxyCommand", null, null)]
    [InlineData("proxyCommand", "", null)]
    [InlineData("unknown", "http://tunnel-user:tunnel-secret@proxy.example:3128", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:3128", 0)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:3128", 1234)]
    [InlineData("proxyCommand", "ftp://tunnel-user:tunnel-secret@proxy.example:21", null)]
    [InlineData("proxyCommand", "proxy.example:3128", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:0", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:65536", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:-1", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:", null)]
    [InlineData("proxyCommand", "socks5://tunnel-user:tunnel-secret@proxy.example", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:3128/path", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:3128?query", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example:3128#fragment", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret secret@proxy.example:3128", null)]
    [InlineData("proxyCommand", "http://tunnel-user:tunnel-secret@proxy.example\\path", null)]
    public void Invalid_OutboundTunnel_Is_Rejected_Before_Save_Without_Exposing_Credentials(string? mode, string? proxyUrl, int? localPort)
    {
        var store = new MachineStore(new HarnessHome(_root));
        var machine = MachineSpec.Ssh("target", "ssh.example", 22, "dev", SshAuth.Agent) with
        {
            OutboundTunnel = new OutboundTunnelSpec(mode!, proxyUrl!, localPort),
        };
        var failure = Assert.Throws<InvalidOperationException>(() => store.Upsert(machine));
        Assert.DoesNotContain("tunnel-user", failure.Message);
        Assert.DoesNotContain("tunnel-secret", failure.Message);
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public void Local_Machines_Cannot_Have_Tunnels(string mode)
    {
        var store = new MachineStore(new HarnessHome(_root));
        Assert.Throws<InvalidOperationException>(() => store.Upsert(MachineSpec.Local("/unused", "second") with
        {
            OutboundTunnel = new OutboundTunnelSpec(mode, "http://proxy.example:3128"),
        }));
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(MachineStore.ValidateOutboundTunnel(MachineSpec.Local()));
    }

    [Theory]
    [InlineData("            mode: proxyCommand\n")]
    [InlineData("            proxyUrl: http://proxy.example:3128\n")]
    [InlineData("            mode: proxyCommand\n            proxyUrl: http://proxy.example:0\n")]
    public void Loading_Rejects_Incomplete_Or_Invalid_OutboundTunnel_Without_Rewriting(string tunnelFields)
    {
        var home = new HarnessHome(_root);
        var document = "fleet:\n  machines:\n    - name: target\n      kind: ssh\n      host: ssh.example\n      outboundTunnel:\n"
            + tunnelFields.Replace("            ", "        ", StringComparison.Ordinal);
        File.WriteAllText(home.SettingsFile, document);
        Assert.Throws<InvalidOperationException>(() => new MachineStore(home).Load());
        Assert.Equal(document, File.ReadAllText(home.SettingsFile));
    }

    [Theory(Timeout = 60000)]
    [InlineData("proxyCommand", null)]
    [InlineData("localForward", null)]
    [InlineData("localForward", 1)]
    [InlineData("localForward", 65535)]
    public async Task Tunnel_Cli_Set_Show_Clear_Use_Local_Settings_And_Redact_Stdin_Authentication(string mode, int? localPort)
    {
        var home = new HarnessHome(_root);
        const string unrelated = "custom_section:\n  retained: value\nnetwork:\n  outbound_proxy:\n    url: http://base.example:3128\n";
        File.WriteAllText(home.SettingsFile, unrelated);
        var store = new MachineStore(home);
        var target = MachineSpec.Ssh("target", "unused.invalid", 22, "dev", SshAuth.Agent) with { Proxy = "existing SSH ProxyCommand" };
        var sibling = MachineSpec.Ssh("sibling", "other.invalid", 22, "dev", SshAuth.Agent);
        store.Save(new FleetConfig([target, sibling], new DeploySettings { ServerProxy = "http://deploy.example:3128" }));
        const string url = "https://tunnel-user:tunnel%2Dsecret@logical-proxy.example:8443";
        var input = $$"""{"mode":"{{mode}}","proxyUrl":"{{url}}","localPort":{{(localPort is null ? "null" : localPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}}}""";
        var set = await FleetRemoteProxyTests.RunCliAsync(_root, input, TestContext.Current.CancellationToken, "machine", "tunnel", "target", "set", "--json-stdin");
        AssertTunnelCliSafeSuccess(set);
        Assert.Equal(target with { OutboundTunnel = new OutboundTunnelSpec(mode, url, localPort) }, store.Load().Find("target"));
        Assert.Equal(sibling, store.Load().Find("sibling"));
        Assert.StartsWith(unrelated, File.ReadAllText(home.SettingsFile), StringComparison.Ordinal);
        var beforeShow = File.ReadAllText(home.SettingsFile);
        var show = await FleetRemoteProxyTests.RunCliAsync(_root, null, TestContext.Current.CancellationToken, "machine", "tunnel", "target", "show");
        AssertTunnelCliSafeSuccess(show);
        using var shown = JsonDocument.Parse(show.Output);
        Assert.Equal(mode, shown.RootElement.GetProperty("mode").GetString());
        Assert.Equal("https://***@logical-proxy.example:8443", shown.RootElement.GetProperty("proxyUrl").GetString());
        if (localPort is { } port)
            Assert.Equal(port, shown.RootElement.GetProperty("localPort").GetInt32());
        Assert.Equal(beforeShow, File.ReadAllText(home.SettingsFile));
        var clear = await FleetRemoteProxyTests.RunCliAsync(_root, null, TestContext.Current.CancellationToken, "machine", "tunnel", "target", "clear");
        AssertTunnelCliSafeSuccess(clear);
        using var cleared = JsonDocument.Parse(clear.Output);
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.ValueKind);
        Assert.Equal(target, store.Load().Find("target"));
        Assert.Equal(sibling, store.Load().Find("sibling"));
        Assert.Equal("http://deploy.example:3128", store.Load().Deploy.ServerProxy);
        Assert.StartsWith(unrelated, File.ReadAllText(home.SettingsFile), StringComparison.Ordinal);
    }

    [Theory(Timeout = 60000)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{invalid tunnel-user:tunnel-secret")]
    [InlineData("{}")]
    [InlineData("""{"mode":"proxyCommand"}""")]
    [InlineData("""{"proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128"}""")]
    [InlineData("""{"mode":"proxyCommand","proxyUrl":null}""")]
    [InlineData("""{"mode":"proxyCommand","proxyUrl":"ftp://tunnel-user:tunnel-secret@proxy.example:21"}""")]
    [InlineData("""{"mode":"proxyCommand","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128","localPort":1080}""")]
    [InlineData("""{"mode":"localForward","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128","localPort":0}""")]
    [InlineData("""{"mode":"localForward","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128","localPort":65536}""")]
    [InlineData("""{"mode":"proxyCommand","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128","unknown":true}""")]
    [InlineData("""{"mode":"proxyCommand","proxyUrl":"http://proxy.example:3128","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128"}""")]
    public async Task Tunnel_Cli_Invalid_Stdin_Is_Private_And_Does_Not_Save(string input)
    {
        var home = new HarnessHome(_root);
        var store = new MachineStore(home);
        store.Upsert(MachineSpec.Ssh("target", "unused.invalid", 22, "dev", SshAuth.Agent));
        var before = File.ReadAllText(home.SettingsFile);
        var result = await FleetRemoteProxyTests.RunCliAsync(_root, input, TestContext.Current.CancellationToken, "machine", "tunnel", "target", "set", "--json-stdin");
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("tunnel-user", result.Output + result.Error);
        Assert.DoesNotContain("tunnel-secret", result.Output + result.Error);
        Assert.Equal(before, File.ReadAllText(home.SettingsFile));
    }

    [Theory(Timeout = 60000)]
    [InlineData("set")]
    [InlineData("set", "--proxy", "http://tunnel-user:tunnel-secret@proxy.example:3128")]
    [InlineData("set", "--proxy-url", "http://tunnel-user:tunnel-secret@proxy.example:3128")]
    [InlineData("show", "--json-stdin")]
    [InlineData("clear", "--probe-url", "http://example.com/")]
    [InlineData("test", "--probe-url", "http://tunnel-user:tunnel-secret@example.com/")]
    [InlineData("test", "--probe-url", "ftp://example.com/")]
    [InlineData("test", "--probe-url", "http://example.com/", "--probe-url", "https://example.com/")]
    [InlineData("test")]
    public async Task Tunnel_Cli_Rejects_Argument_Authentication_And_Invalid_Operations_Without_Connecting(params string[] operation)
    {
        var home = new HarnessHome(_root);
        var store = new MachineStore(home);
        store.Upsert(MachineSpec.Ssh("target", "unused.invalid", 22, "dev", SshAuth.Agent));
        var before = File.ReadAllText(home.SettingsFile);
        var result = await FleetRemoteProxyTests.RunCliAsync(_root, null, TestContext.Current.CancellationToken, ["machine", "tunnel", "target", .. operation]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("tunnel-user", result.Output + result.Error);
        Assert.DoesNotContain("tunnel-secret", result.Output + result.Error);
        Assert.Equal(before, File.ReadAllText(home.SettingsFile));
    }

    [Fact(Timeout = 60000)]
    public async Task Tunnel_Cli_Cannot_Configure_The_Implicit_Local_Machine()
    {
        var home = new HarnessHome(_root);
        File.WriteAllText(home.SettingsFile, "custom_section:\n  retained: value\n");
        var before = File.ReadAllText(home.SettingsFile);
        var result = await FleetRemoteProxyTests.RunCliAsync(_root, """{"mode":"proxyCommand","proxyUrl":"http://tunnel-user:tunnel-secret@proxy.example:3128"}""", TestContext.Current.CancellationToken, "machine", "tunnel", "local", "set", "--json-stdin");
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("tunnel-secret", result.Output + result.Error);
        Assert.Equal(before, File.ReadAllText(home.SettingsFile));
    }

    private static void AssertTunnelCliSafeSuccess(FleetCliProcess.Result result)
    {
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.DoesNotContain("tunnel-user", result.Output + result.Error);
        Assert.DoesNotContain("tunnel-secret", result.Output + result.Error);
        Assert.DoesNotContain("tunnel%2Dsecret", result.Output + result.Error);
    }

}
