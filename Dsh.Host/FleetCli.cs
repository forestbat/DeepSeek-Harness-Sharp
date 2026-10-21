using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.Runtime;

namespace Dsh.Host;

/**
 * `dsharp machine | session | doctor`: the machine list and the sessions of every machine from the command line.
 * Everything goes through Dsh.Fleet, so the CLI reads and writes the same machine list and connects the same way as the GUI.
 */
internal static class FleetCli
{
    private const string MachineCommand = "machine";
    private const string SessionCommand = "session";
    private const string DoctorCommand = "doctor";
    private const string RemoteCommand = "remote";

    /** The shell convention for a command stopped by Ctrl+C (128 + SIGINT). */
    private const int InterruptedExitCode = 130;

    private static readonly HashSet<string> Flags = ["--local", "--force", "--password-stdin"];

    private static readonly HashSet<string> ValueOptions =
        ["--host", "--port", "--user", "--auth", "--key", "--proxy", "--remote-path", "--remote-home", "--ssh-option", "--home", "--machine", "--endpoint-id", "--relay-url", "--ssh-port", "--activation"];

    private static readonly FleetTunnelJsonContext TunnelJson = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    });

    public static bool Handles(string[] args) => args is [MachineCommand or SessionCommand or DoctorCommand or RemoteCommand, ..];

    /** resumeHarness opens a harness session of the local machine in this terminal's TUI. */
    public static async Task<int> RunAsync(HarnessHome home, string[] args, Func<HarnessHome, string, Task<int>> resumeHarness)
    {
        var isProxyCommand = args is [MachineCommand, "proxy", ..];
        var isTunnelCommand = args is [MachineCommand, "tunnel", ..];
        var hasPrivateInput = isProxyCommand || isTunnelCommand;
        var parsed = FleetArguments.Parse(args.Skip(1),
            hasPrivateInput ? [.. Flags, "--json-stdin"] : Flags,
            hasPrivateInput ? [.. ValueOptions, "--probe-url"] : ValueOptions);
        if (parsed.Error is { } error)
            return await FailAsync(isProxyCommand ? "invalid machine proxy options (try --help)"
                : isTunnelCommand ? "invalid machine tunnel options (try --help)" : error);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        await using var context = new FleetCliContext(home, new MachineStore(home), parsed, resumeHarness, cancellation.Token);
        try
        {
            return args[0] switch
            {
                MachineCommand => await RunMachineAsync(context),
                SessionCommand => await RunSessionAsync(context),
                RemoteCommand => await RunReceiverAsync(context),
                _ => await RunDoctorAsync(context),
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return InterruptedExitCode;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or NotSupportedException or JsonRpcException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
        {
            return await FailAsync(failure.Message);
        }
    }

    public static string Usage => """
               dsharp machine list
               dsharp machine add <name> --host <h> [--port N] [--user U] [--auth agent|key|password] [--key <path>] [--proxy <cmd>]
                                         [--remote-path <dir>] [--remote-home <dir>] [--ssh-option <o>]... [--password-stdin]
               dsharp machine add <name> --local --home <harness home>
               dsharp machine add <name> --endpoint-id <id> [--relay-url <url>]... [SSH authentication options]
               dsharp machine enable-iroh <name> [--relay-url <url>]...   (install over existing SSH, verify, then switch)
               dsharp machine remove <name>
               dsharp machine test <name>               (connect without deploying)
               dsharp machine deploy <name> [--force]   (install or upgrade dsharp on the machine)
               dsharp machine proxy <name> show
               dsharp machine proxy <name> set --json-stdin
               dsharp machine proxy <name> clear
               dsharp machine proxy <name> test [--probe-url <url>]
               dsharp machine tunnel <name> set --json-stdin
               dsharp machine tunnel <name> show
               dsharp machine tunnel <name> clear
               dsharp machine tunnel <name> test [--probe-url <url>]
               dsharp machine tunnel <name> use         (select this machine for this home's outbound requests)
               dsharp machine tunnel direct             (restore the local baseline without opening SSH)
               dsharp session list [--machine <name>]
               dsharp session attach <machine>/<session>   (terminal sessions; leave with Ctrl+])
               dsharp session stop <machine>/<session>
               dsharp doctor [--machine <name>]
               dsharp remote enable [--ssh-port N] [--relay-url <url>]...   (enable this machine's receiver)
               dsharp remote info
               dsharp remote disable
        """;

    private static Task<int> RunMachineAsync(FleetCliContext context) => context.Arguments.Subcommand switch
    {
        "list" or null => ListMachinesAsync(context),
        "add" => AddMachineAsync(context),
        "remove" => RemoveMachineAsync(context),
        "test" => TestMachineAsync(context),
        "deploy" => DeployMachineAsync(context),
        "enable-iroh" => EnableIrohAsync(context),
        "proxy" => RunMachineProxyAsync(context),
        "tunnel" => RunMachineTunnelAsync(context),
        var other => FailAsync($"unknown machine subcommand \"{other}\" (list | add | remove | test | deploy | enable-iroh | proxy | tunnel)"),
    };

    private static Task<int> RunSessionAsync(FleetCliContext context) => context.Arguments.Subcommand switch
    {
        "list" or null => ListSessionsAsync(context),
        "attach" => AttachSessionAsync(context),
        "stop" => StopSessionAsync(context),
        var other => FailAsync($"unknown session subcommand \"{other}\" (list | attach | stop)"),
    };

    private static async Task<int> ListMachinesAsync(FleetCliContext context)
    {
        var rows = context.Store.Load().AllMachines()
            .Select(machine => new[] { machine.Name, machine.Kind, machine.Target, machine.IsLocal ? "-" : machine.Auth, machine.HarnessHome ?? "(default)" })
            .ToList();
        await WriteTableAsync(["NAME", "KIND", "TARGET", "AUTH", "HARNESS HOME"], rows);
        return 0;
    }

    private static async Task<int> AddMachineAsync(FleetCliContext context)
    {
        var arguments = context.Arguments;
        if (arguments.Operand is not { } name)
            return await FailAsync("machine add requires a name");
        if (arguments.Has("--local") && arguments.Value("--endpoint-id") is not null)
            throw new InvalidOperationException("--local and --endpoint-id cannot be combined");
        var machine = arguments.Has("--local")
            ? MachineSpec.Local(arguments.Value("--home") is { } home ? Path.GetFullPath(home) : throw new InvalidOperationException("machine add --local requires --home <harness home>"), name)
            : await SshMachineAsync(name, arguments);
        context.Store.Upsert(machine);
        await Console.Out.WriteLineAsync($"saved {machine.Name} ({machine.Target}) to {context.Store.FilePath}");
        return 0;
    }

    private static async Task<MachineSpec> SshMachineAsync(string name, FleetArguments arguments)
    {
        var endpoint = arguments.Value("--endpoint-id");
        var host = arguments.Value("--host") ?? (endpoint is not null ? "" : throw new InvalidOperationException("machine add requires --host, --endpoint-id or --local --home"));
        var port = Port(arguments, "--port");
        var key = arguments.Value("--key");
        var auth = arguments.Value("--auth") ?? (key is not null ? SshAuth.Key : arguments.Has("--password-stdin") ? SshAuth.Password : SshAuth.Agent);
        if (auth is not (SshAuth.Agent or SshAuth.Key or SshAuth.Password))
            throw new InvalidOperationException($"invalid --auth \"{auth}\" (agent | key | password)");
        var password = arguments.Has("--password-stdin") ? await Console.In.ReadLineAsync() : null;
        return MachineSpec.Ssh(name, host, port, arguments.Value("--user") ?? Environment.UserName, auth) with
        {
            KeyPath = key is null ? null : Path.GetFullPath(key),
            Proxy = arguments.Value("--proxy"),
            RemotePath = arguments.Value("--remote-path"),
            HarnessHome = arguments.Value("--remote-home"),
            Password = password,
            SshOptions = arguments.Values("--ssh-option") is { Count: > 0 } options ? options : null,
            Kind = endpoint is null ? MachineKinds.Ssh : MachineKinds.Iroh,
            EndpointId = endpoint,
            RelayUrls = arguments.Values("--relay-url") is { Count: > 0 } relays ? relays : null,
        };
    }

    private static async Task<int> RemoveMachineAsync(FleetCliContext context)
    {
        if (context.Arguments.Operand is not { } name)
            return await FailAsync("machine remove requires a name");
        if (!context.Store.Remove(name))
            return await FailAsync($"no machine named \"{name}\"");
        await Console.Out.WriteLineAsync($"removed {name}");
        return 0;
    }

    /** Connects once without deploying or starting anything and reports what answered. */
    private static async Task<int> TestMachineAsync(FleetCliContext context)
    {
        var machine = context.RequireMachine(context.Arguments.Operand);
        await using var connection = new MachineConnection(machine, context.Options(deploy: false, startLocalHost: false));
        connection.Start();
        await connection.WaitSettledAsync(context.Cancellation);
        if (connection.State != MachineConnectionState.Connected)
            return await FailAsync($"{machine.Name} ({machine.Target}): {connection.Detail}");
        var info = connection.Info!;
        await Console.Out.WriteLineAsync($"{machine.Name} ({machine.Target}): host {info.HostVersion} on {info.Platform}, harness home {info.HomeRoot}, {connection.Sessions.Count} session(s)");
        return 0;
    }

    private static async Task<int> RunMachineProxyAsync(FleetCliContext context)
    {
        var arguments = context.Arguments;
        var operation = arguments.Positional.ElementAtOrDefault(2);
        if (arguments.Positional.Count != 3 || operation is not ("show" or "set" or "clear" or "test")
            || arguments.Flags.Any(flag => flag != "--json-stdin" || operation != "set")
            || arguments.Options.Keys.Any(option => option != "--probe-url" || operation != "test")
            || operation == "set" && !arguments.Has("--json-stdin"))
            return await FailAsync("usage: machine proxy <name> show | set --json-stdin | clear | test [--probe-url <url>]");

        RemoteOutboundProxySettings? supplied = null;
        var probeUrl = arguments.Value("--probe-url");
        try
        {
            if (operation == "set")
            {
                try
                {
                    supplied = JsonSerializer.Deserialize(await Console.In.ReadToEndAsync(context.Cancellation),
                        NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
                }
                catch (JsonException)
                {
                    return await FailAsync("machine proxy set requires a valid JSON settings object on stdin");
                }
                if (supplied is null)
                    return await FailAsync("machine proxy set requires a JSON settings object on stdin");
            }

            var machine = context.RequireMachine(arguments.Operand);
            await using var connection = new MachineConnection(machine, context.Options(deploy: false, startLocalHost: true));
            connection.Start();
            var network = new FleetNetwork(connection);
            if (operation == "test")
            {
                var result = await network.ProbeOutboundAsync(new RemoteOutboundProxyProbeRequest(ProbeUrl: probeUrl), context.Cancellation);
                var message = result.Message;
                if (!string.IsNullOrEmpty(probeUrl))
                    message = message.Replace(probeUrl, RedactProxyUrl(probeUrl), StringComparison.Ordinal);
                await Console.Out.WriteLineAsync(OutboundProxy.Redact(message));
                return result.Reachable ? 0 : 1;
            }

            var state = await network.GetOutboundAsync(context.Cancellation);
            if (operation is "set" or "clear")
                state = await network.SetOutboundAsync(new RemoteOutboundProxySetRequest(state.Revision,
                    supplied ?? new RemoteOutboundProxySettings()), context.Cancellation);
            var settings = state.Settings;
            var redacted = state with
            {
                Settings = settings with
                {
                    Url = settings.Url is { } url ? RedactProxyUrl(url) : null,
                    ProbeUrl = settings.ProbeUrl is { } savedProbeUrl ? RedactProxyUrl(savedProbeUrl) : null,
                },
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(redacted, NetworkProtocolJsonContext.Default.RemoteOutboundProxyState));
            return 0;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or NotSupportedException or JsonRpcException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
        {
            // Do not rely on URI parsing to hide credentials in malformed user-supplied URLs.
            var message = failure.Message;
            if (supplied?.Url is { Length: > 0 } suppliedUrl)
                message = message.Replace(suppliedUrl, "[redacted URL]", StringComparison.Ordinal);
            if (supplied?.ProbeUrl is { Length: > 0 } suppliedProbeUrl)
                message = message.Replace(suppliedProbeUrl, "[redacted URL]", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(probeUrl))
                message = message.Replace(probeUrl, "[redacted URL]", StringComparison.Ordinal);
            return await FailAsync(OutboundProxy.Redact(message));
        }
    }

    private static async Task<int> RunMachineTunnelAsync(FleetCliContext context)
    {
        var arguments = context.Arguments;
        var operation = arguments.Positional.ElementAtOrDefault(2);
        var direct = arguments.Positional is ["tunnel", "direct"] && arguments.Flags.Count == 0 && arguments.Options.Count == 0;
        if (!direct && (arguments.Positional.Count != 3 || operation is not ("show" or "set" or "clear" or "test" or "use")
            || arguments.Flags.Any(flag => flag != "--json-stdin" || operation != "set")
            || arguments.Options.Any(option => option.Key != "--probe-url" || operation != "test" || option.Value.Count != 1)
            || operation == "set" && !arguments.Has("--json-stdin")))
            return await FailAsync("usage: machine tunnel <name> set --json-stdin | show | clear | test [--probe-url <url>] | use; machine tunnel direct");

        OutboundTunnelSpec? supplied = null;
        OutboundTunnelSpec? saved = null;
        var probeUrl = arguments.Value("--probe-url") ?? "https://example.com/";
        try
        {
            if (direct || operation == "use")
            {
                await context.OutboundRoute.SelectAsync(direct ? null : arguments.Operand, context.Cancellation);
                await Console.Out.WriteLineAsync(direct ? "Local outbound selection cleared." : "Local outbound selection saved.");
                return 0;
            }
            var machine = context.RequireMachine(arguments.Operand);
            saved = machine.OutboundTunnel;
            if (machine.IsLocal)
                return await FailAsync("outbound tunnels are not available for local machines");
            if (operation == "set")
            {
                try
                {
                    supplied = JsonSerializer.Deserialize(await Console.In.ReadToEndAsync(context.Cancellation), TunnelJson.OutboundTunnelSpec);
                }
                catch (JsonException)
                {
                    return await FailAsync("machine tunnel set requires a valid JSON settings object on stdin");
                }
                if (supplied is null)
                    return await FailAsync("machine tunnel set requires a JSON settings object on stdin");
                machine = machine with { OutboundTunnel = supplied };
                context.Store.Upsert(machine);
            }
            else if (operation == "clear")
            {
                machine = machine with { OutboundTunnel = null };
                context.Store.Upsert(machine);
            }
            else if (operation == "test")
            {
                if (!Uri.TryCreate(probeUrl, UriKind.Absolute, out var destination)
                    || destination.Scheme is not ("http" or "https") || destination.Host.Length == 0
                    || destination.UserInfo.Length > 0 || destination.Fragment.Length > 0)
                    return await FailAsync("machine tunnel test requires an absolute HTTP(S) probe URL without credentials or a fragment");
                if (MachineStore.ValidateOutboundTunnel(machine) is null)
                    return await FailAsync("machine has no outbound tunnel configured");
                await using var tunnel = new SshOutboundTunnel(machine);
                using var outbound = new OutboundProxy();
                outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
                if (outbound.CaptureRoute(destination).ProxyUri is null)
                    return await FailAsync("probe URL bypasses the configured tunnel; use a public HTTP(S) destination");
                using var client = new HttpClient(outbound.CreateHandler());
                using var response = await client.GetAsync(destination, HttpCompletionOption.ResponseHeadersRead, context.Cancellation);
                await using var body = await response.Content.ReadAsStreamAsync(context.Cancellation);
                await body.CopyToAsync(Stream.Null, context.Cancellation);
                await Console.Out.WriteLineAsync($"HTTP {(int)response.StatusCode} through {outbound.Describe()} (response consumed)");
                return response.IsSuccessStatusCode ? 0 : 1;
            }

            var redacted = machine.OutboundTunnel is { } settings
                ? settings with { ProxyUrl = RedactProxyUrl(settings.ProxyUrl) }
                : null;
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(redacted, TunnelJson.OutboundTunnelSpec));
            return 0;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return await FailAsync("could not read machine tunnel settings: invalid YAML");
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            return await FailAsync("machine tunnel test timed out");
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or NotSupportedException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
        {
            var message = failure.Message;
            foreach (var url in new[] { supplied?.ProxyUrl, saved?.ProxyUrl, probeUrl })
            {
                if (string.IsNullOrEmpty(url))
                    continue;
                message = message.Replace(url, "[redacted URL]", StringComparison.Ordinal);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
                    foreach (var part in uri.UserInfo.Split(':', 2))
                    {
                        if (part.Length == 0)
                            continue;
                        message = message.Replace(part, "***", StringComparison.Ordinal);
                        message = message.Replace(Uri.UnescapeDataString(part), "***", StringComparison.Ordinal);
                    }
            }
            return await FailAsync(OutboundProxy.Redact(message));
        }
    }

    /** Redacts field values even when whitespace makes their user info an invalid URI. */
    private static string RedactProxyUrl(string url)
    {
        var at = url.LastIndexOf('@');
        if (at < 0)
            return url;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? 0 : scheme + 3;
        return at < start ? url : string.Concat(url.AsSpan(0, start), "***@", url.AsSpan(at + 1));
    }

    private static async Task<int> DeployMachineAsync(FleetCliContext context)
    {
        var machine = context.RequireMachine(context.Arguments.Operand);
        if (machine.IsLocal)
            return await FailAsync($"{machine.Name} is this computer; it runs this dsharp and needs no deployment");
        var deploy = context.Store.Load().Deploy;
        var result = await new RemoteDsharpInstaller(new SshTransport(machine)).EnsureAsync(
            deploy, step => Console.Error.WriteLine(step), context.Cancellation, force: context.Arguments.Has("--force"),
            localHome: context.Home);
        await Console.Out.WriteLineAsync(result.AlreadyPresent
            ? $"{machine.Name}: {result.CommandPath} is already at {RemoteHostServer.ReleaseVersion} or newer (use --force to redeploy)"
            : $"{machine.Name}: deployed {RemoteHostServer.ReleaseVersion} to {result.CommandPath}");
        return 0;
    }

    private static async Task<int> EnableIrohAsync(FleetCliContext context)
    {
        var machine = context.RequireMachine(context.Arguments.Operand);
        if (context.Arguments.Values("--relay-url") is { Count: > 0 } relays)
            machine = machine with { RelayUrls = relays };
        List<string> progress = [];
        MachineStore.Validate(context.Store.Load().Machines);
        var converted = await IrohRemoteInstaller.EnableAndSaveAsync(machine, context.Options(deploy: true, startLocalHost: false), result => context.Store.Upsert(result), progress.Add, context.Cancellation);
        await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, progress));
        await Console.Out.WriteLineAsync($"saved {converted.Name} ({converted.Target}) after verifying SSH-over-iroh");
        return 0;
    }

    private static async Task<int> RunReceiverAsync(FleetCliContext context)
    {
        var activationText = context.Arguments.Value("--activation");
        var activation = activationText is null ? (Guid?)null : Guid.TryParseExact(activationText, "N", out var id) ? id : throw new ArgumentException("Invalid receiver activation identifier.");
        if (context.Arguments.Subcommand == "disable")
        {
            var disabled = await IrohReceiver.DisableAsync(context.Home, activation, context.Cancellation);
            await Console.Out.WriteLineAsync(disabled.Running ? "receiver unchanged; this activation does not own it" : "receiver disabled; identity retained, SSH service and Host sessions unchanged");
            return 0;
        }
        var status = context.Arguments.Subcommand switch
        {
            "enable" => await IrohReceiver.EnableAsync(context.Home, Port(context.Arguments, "--ssh-port"), context.Arguments.Values("--relay-url"), activation, context.Cancellation),
            "info" => await IrohReceiver.InfoAsync(context.Home, context.Cancellation),
            _ => throw new InvalidOperationException("remote requires a subcommand (enable | info | disable)"),
        };
        await Console.Out.WriteLineAsync($"endpointId={status.EndpointId}");
        await Console.Out.WriteLineAsync($"running={status.Running.ToString().ToLowerInvariant()}");
        await Console.Out.WriteLineAsync($"service={status.ServiceDescription}");
        return 0;
    }

    private static int Port(FleetArguments arguments, string option)
    {
        if (arguments.Value(option) is not { } text)
            return MachineSpec.DefaultSshPort;
        return int.TryParse(text, out var port) && port is > 0 and <= ushort.MaxValue
            ? port : throw new InvalidOperationException($"{option} must be a port between 1 and {ushort.MaxValue}");
    }

    private static async Task<int> ListSessionsAsync(FleetCliContext context)
    {
        await using var fleet = await context.ConnectAsync(context.SelectedMachines());
        var pending = fleet.Pending().CountBy(item => (item.Machine, item.SessionId)).ToDictionary();
        var rows = fleet.Sessions()
            .Select(item => new[]
            {
                item.Machine, item.Session.SessionId, item.Session.Kind, item.Session.AgentKind ?? "-", item.Session.Status,
                Math.Max(item.Session.PendingCount, pending.GetValueOrDefault((item.Machine, item.Session.SessionId))).ToString(),
                item.Session.ProjectPath ?? "-",
            })
            .ToList();
        await WriteTableAsync(["MACHINE", "SESSION", "TYPE", "AGENT", "STATUS", "PENDING", "PROJECT"], rows);
        return await ReportUnreachableAsync(fleet);
    }

    private static async Task<int> AttachSessionAsync(FleetCliContext context)
    {
        var (connection, session) = await context.FindSessionAsync();
        await using (connection)
        {
            var lease = await connection.WaitConnectedAsync(context.Cancellation);
            if (session.Kind is HostSessionKinds.Terminal or HostSessionKinds.External)
                return await AttachTerminalAsync(lease.Client.Pty, connection.Machine, session, context.Cancellation);
            if (session.Kind != HostSessionKinds.Harness)
                return await FailAsync($"{session.Kind} sessions have no terminal to attach to; open them in the GUI");
            if (!connection.Machine.IsLocal)
                return await FailAsync($"harness sessions of {connection.Machine.Name} open in the GUI (dsharp gui); the command line attaches terminal sessions only");
        }

        return await context.ResumeHarness(context.HomeOf(connection.Machine), session.SessionId);
    }

    private static async Task<int> AttachTerminalAsync(IRemotePty pty, MachineSpec machine, RemoteHostSession session, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync($"dsharp: attaching to {machine.Name}/{session.SessionId}; leave with {ConsoleTunnel.DetachKeyName} (the session keeps running)");
        await ConsoleTunnel.RunAsync(
            (input, output, token) => pty.AttachPtyAsync(session.SessionId, input, output, token),
            (columns, rows, token) => pty.ResizePtyAsync(session.SessionId, columns, rows, token),
            cancellationToken);
        await Console.Out.WriteLineAsync($"dsharp: detached from {machine.Name}/{session.SessionId}");
        return 0;
    }

    private static async Task<int> StopSessionAsync(FleetCliContext context)
    {
        var (connection, session) = await context.FindSessionAsync();
        await using (connection)
        {
            var client = (await connection.WaitConnectedAsync(context.Cancellation)).Client;
            var target = $"{connection.Machine.Name}/{session.SessionId}";
            switch (session.Kind)
            {
                case HostSessionKinds.Terminal:
                    await client.Pty.StopPtyAsync(session.SessionId, context.Cancellation);
                    await Console.Out.WriteLineAsync($"stopped {target}");
                    return 0;
                case HostSessionKinds.Harness:
                    await client.InterruptAsync(session.SessionId, context.Cancellation);
                    await Console.Out.WriteLineAsync($"interrupted the running turn of {target}");
                    return 0;
                case HostSessionKinds.Acp:
                case HostSessionKinds.External:
                    await client.Agent.StopAgentAsync(session.SessionId, context.Cancellation);
                    await Console.Out.WriteLineAsync($"stopped {target}");
                    return 0;
                default:
                    return await FailAsync($"unknown session kind {session.Kind} for {target}");
            }
        }
    }

    private static async Task<int> RunDoctorAsync(FleetCliContext context)
    {
        var failed = false;
        foreach (var machine in context.SelectedMachines())
        {
            await Console.Out.WriteLineAsync($"{machine.Name} ({machine.Target})");
            var checks = await FleetDoctor.CheckAsync(machine, context.Options(deploy: false, startLocalHost: false), context.Cancellation);
            foreach (var check in checks)
            {
                await Console.Out.WriteLineAsync($"  [{check.Outcome.ToString().ToLowerInvariant()}] {check.Name}: {check.Detail}");
                if (check.Fix is { } fix)
                    await Console.Out.WriteLineAsync($"      fix: {fix}");
            }

            failed |= checks.Any(check => check.Outcome == DoctorOutcome.Failed);
        }

        return failed ? 1 : 0;
    }

    private static async Task<int> ReportUnreachableAsync(FleetClient fleet)
    {
        var offline = fleet.Statuses().Where(status => status.State != MachineConnectionState.Connected).ToList();
        foreach (var status in offline)
            await Console.Error.WriteLineAsync($"unreachable: {status.Machine} ({status.Target}): {status.Detail}");
        return offline.Count == 0 ? 0 : 1;
    }

    private static async Task WriteTableAsync(string[] header, List<string[]> rows)
    {
        var widths = header.Select((title, column) => rows.Select(row => row[column].Length).Append(title.Length).Max()).ToArray();
        foreach (var row in rows.Prepend(header))
            await Console.Out.WriteLineAsync(string.Join("  ", row.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd());
    }

    private static async Task<int> FailAsync(string message)
    {
        await Console.Error.WriteLineAsync($"dsharp: {message}");
        return 1;
    }
}

/** Positional arguments, flags and repeatable value options of one fleet command; Error is set for unknown or incomplete options. */
internal sealed record FleetArguments(List<string> Positional, HashSet<string> Flags, Dictionary<string, List<string>> Options, string? Error)
{
    public string? Subcommand => Positional.FirstOrDefault();

    public string? Operand => Positional.Skip(1).FirstOrDefault();

    public bool Has(string flag) => Flags.Contains(flag);

    public string? Value(string option) => Options.GetValueOrDefault(option)?.LastOrDefault();

    public List<string> Values(string option) => Options.GetValueOrDefault(option) ?? [];

    public static FleetArguments Parse(IEnumerable<string> args, HashSet<string> knownFlags, HashSet<string> valueOptions)
    {
        var parsed = new FleetArguments([], new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, List<string>>(StringComparer.Ordinal), null);
        using var cursor = args.GetEnumerator();
        while (cursor.MoveNext())
        {
            var argument = cursor.Current;
            if (knownFlags.Contains(argument))
                parsed.Flags.Add(argument);
            else if (valueOptions.Contains(argument))
            {
                if (!cursor.MoveNext())
                    return parsed with { Error = $"{argument} requires a value" };
                parsed.Options.TryAdd(argument, []);
                parsed.Options[argument].Add(cursor.Current);
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
                return parsed with { Error = $"unknown option \"{argument}\" (try --help)" };
            else
                parsed.Positional.Add(argument);
        }

        return parsed;
    }
}

/** What every fleet command works with: the harness home and its machine list, the parsed arguments and the Ctrl+C token. */
internal sealed record FleetCliContext(
    HarnessHome Home,
    MachineStore Store,
    FleetArguments Arguments,
    Func<HarnessHome, string, Task<int>> ResumeHarness,
    CancellationToken Cancellation) : IAsyncDisposable
{
    private BorrowedOutboundRoute? _outboundRoute;

    public BorrowedOutboundRoute OutboundRoute => _outboundRoute ??= new BorrowedOutboundRoute(Home, new OutboundProxy());

    public async ValueTask DisposeAsync()
    {
        if (_outboundRoute is not { } route)
            return;
        try
        {
            await route.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            route.Proxy.Dispose();
        }
    }

    /** The CLI connects once per command: no reconnects, the stored deployment preferences, and a local host started on demand like ssh starts a remote one. */
    public FleetOptions Options(bool deploy, bool startLocalHost)
    {
        var settings = Store.Load().Deploy;
        return new FleetOptions
        {
            ClientName = $"cli@{Environment.MachineName}",
            Deploy = deploy ? settings : settings with { AutoDeploy = false },
            // Only an actual local archive download acquires this home's borrowed route.
            LocalHome = Home,
            AutoReconnect = false,
            StartLocalHost = startLocalHost ? StartLocalHostAsync : null,
        };
    }

    public MachineSpec RequireMachine(string? name)
        => name is null
            ? throw new InvalidOperationException("a machine name is required (see dsharp machine list)")
            : Store.Load().Find(name) ?? throw new InvalidOperationException($"no machine named \"{name}\" (see dsharp machine list)");

    /** --machine narrows to one machine; otherwise this computer and every stored machine. */
    public IReadOnlyList<MachineSpec> SelectedMachines()
        => Arguments.Value("--machine") is { } name ? [RequireMachine(name)] : Store.Load().AllMachines();

    /** Connects to the given machines and waits until each has connected or failed. */
    public async Task<FleetClient> ConnectAsync(IReadOnlyList<MachineSpec> machines)
    {
        var fleet = new FleetClient(machines, Options(deploy: true, startLocalHost: true));
        foreach (var machine in fleet.Machines)
            machine.StateChanged += ReportDeployment;
        fleet.Start();
        await fleet.WaitSettledAsync(Cancellation);
        return fleet;
    }

    /** Resolves `<machine>/<session>` on a connected machine; the caller owns the returned connection. */
    public async Task<(MachineConnection Connection, RemoteHostSession Session)> FindSessionAsync()
    {
        var target = Arguments.Operand ?? throw new InvalidOperationException("a target <machine>/<session> is required (see dsharp session list)");
        var cut = target.IndexOf('/');
        if (cut <= 0 || cut == target.Length - 1)
            throw new InvalidOperationException($"\"{target}\" is not <machine>/<session>");
        var machine = RequireMachine(target[..cut]);
        var connection = new MachineConnection(machine, Options(deploy: true, startLocalHost: true));
        connection.StateChanged += ReportDeployment;
        connection.Start();
        await connection.WaitSettledAsync(Cancellation);
        var sessionId = target[(cut + 1)..];
        var session = connection.Sessions.FirstOrDefault(item => item.SessionId == sessionId);
        if (connection.State == MachineConnectionState.Connected && session is not null)
            return (connection, session);
        var problem = connection.State == MachineConnectionState.Connected
            ? $"{machine.Name} has no session \"{sessionId}\" (see dsharp session list --machine {machine.Name})"
            : $"{machine.Name} ({machine.Target}) is unreachable: {connection.Detail}";
        await connection.DisposeAsync();
        throw new InvalidOperationException(problem);
    }

    public HarnessHome HomeOf(MachineSpec machine) => machine.HarnessHome is { } home ? HarnessHome.Resolve(home) : Home;

    private static Task StartLocalHostAsync(MachineSpec machine, CancellationToken cancellationToken)
        => HostAutostart.EnsureRunningAsync(HarnessHome.Resolve(machine.HarnessHome), token: null, cancellationToken);

    private static void ReportDeployment(MachineConnection machine)
    {
        if (machine.State == MachineConnectionState.Deploying && machine.Detail is { } step)
            Console.Error.WriteLine($"{machine.Machine.Name}: {step}");
    }
}

[JsonSerializable(typeof(OutboundTunnelSpec))]
internal partial class FleetTunnelJsonContext : JsonSerializerContext;
