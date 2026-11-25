using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dsh.Gui.ViewModels;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Integration;

internal static class GuiManagementScenarios
{
    private const int MaximumAgentTasks = 3;
    private const int InvitationLifetimeMilliseconds = 300_000;
    private const int CertificateKeyBits = 2048;
    private const string PublicRelayUrl = "https://use1-1.relay.n0.iroh.link";
    private const string AgentDescription = "GUI native definition acceptance";
    private static readonly string[] AgentArguments = ["--gui-acceptance", "typed argument 中文"];
    private static readonly string[] AgentTools = ["files", "transfer"];
    private static readonly (string Name, string Value)[] AgentEnvironment =
        [("DSH_GUI_ACCEPTANCE", "typed value 中文"), ("DSH_GUI_SECOND", "value with spaces")];

    public static async Task<string> StartAgentAsync(Ma2aViewModel target, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var name = $"gui-{Guid.NewGuid():N}";
        var cwd = Directory.CreateDirectory(Path.Combine(pair.Root, name)).FullName;
        var program = AgentServiceScenarios.ExampleBinary(pair, "ma2a-example-agent");
        try
        {
            FillAgentDefinition(target, name, program, cwd);
            await ExecuteAsync(target, "agent_defined", cancellationToken);
            VerifyAgentDefinition(await FindAgentAsync(target, name, cancellationToken), name, program, cwd);
            GuiScenarios.SelectManagement(target, "agent_start");
            Text(target, "agent", name);
            await ExecuteAsync(target, "agent_started", cancellationToken);
            await WaitForReadbackAsync(() => FindAgentAsync(target, name, cancellationToken), AgentReady,
                "The GUI-started SDK Agent must reach an authoritative ready instance.", cancellationToken);
            GuiScenarios.SelectManagement(target, "service_expose");
            Text(target, "space_id", pair.SpaceId);
            Text(target, "kind", "agent");
            Text(target, "agent", name);
            await ExecuteAsync(target, "service_exposed", cancellationToken);
            NativeRuntimePair.Check(await HasExposureAsync(target, pair.SpaceId, "agent", name, cancellationToken), "The GUI must expose only its new Agent.");
            await NativeRuntimePair.PassedAsync("GUI defines a real SDK Agent with nonempty args/env, nullable cwd, multiple tools and bounded concurrency, then starts and exposes its ready instance.");
            return name;
        }
        catch
        {
            using var cleanup = new Ma2aViewModel { StateDirectory = target.StateDirectory, Executable = target.Executable };
            await StopAgentAsync(cleanup, pair, name, CancellationToken.None);
            throw;
        }
    }

    private static void FillAgentDefinition(Ma2aViewModel target, string name, string program, string cwd)
    {
        GuiScenarios.SelectManagement(target, "agent_define");
        Text(target, "agent", name);
        Text(target, "description", AgentDescription);
        Text(target, "program", program);
        NativeRuntimePair.Check(Field(target, "cwd").IsNullable && Field(target, "cwd").BuildValue() is null, "The Agent working directory must initially preserve the schema's nullable value.");
        Text(target, "cwd", cwd);
        foreach (var argument in AgentArguments) GuiScenarios.AddArgument(Field(target, "args"), argument);
        foreach (var tool in AgentTools) GuiScenarios.AddArgument(Field(target, "tools"), tool);
        var environment = Field(target, "env");
        foreach (var pair in AgentEnvironment)
        {
            environment.AddItemCommand.Execute(null);
            var item = environment.Items[^1];
            item.Children.Single(field => field.Name == "name").Text = pair.Name;
            item.Children.Single(field => field.Name == "value").Text = pair.Value;
        }
        var built = environment.BuildValue()!.AsArray();
        NativeRuntimePair.Check(built.Count == AgentEnvironment.Length && built.Select((item, index) =>
                item!["name"]!.GetValue<string>() == AgentEnvironment[index].Name && item["value"]!.GetValue<string>() == AgentEnvironment[index].Value).All(valid => valid),
            "The actual recursive GUI field builder must retain both environment names and values.");
        Field(target, "max_tasks").Number = MaximumAgentTasks;
    }

    private static void VerifyAgentDefinition(JsonElement agent, string name, string program, string cwd)
    {
        NativeRuntimePair.Check(agent.GetProperty("agent").GetString() == name && agent.GetProperty("description").GetString() == AgentDescription,
            "The Runtime must retain the GUI Agent identity and description.");
        NativeRuntimePair.Check(agent.GetProperty("program").GetString() == program && agent.GetProperty("cwd").GetString() == cwd,
            "The Runtime must retain the GUI program and enabled nullable working directory.");
        NativeRuntimePair.Check(agent.GetProperty("args").EnumerateArray().Select(item => item.GetString()).SequenceEqual(AgentArguments), "GUI arguments must retain their boundaries and UTF-8 text.");
        NativeRuntimePair.Check(agent.GetProperty("tools").EnumerateArray().Select(item => item.GetString()).Order().SequenceEqual(AgentTools.Order()), "The Runtime must retain both selected tool families.");
        NativeRuntimePair.Check(agent.GetProperty("env_names").EnumerateArray().Select(item => item.GetString()).Order().SequenceEqual(AgentEnvironment.Select(pair => pair.Name).Order()),
            "The public Agent view must retain environment names without returning secret values.");
        NativeRuntimePair.Check(!agent.GetProperty("elided").GetBoolean() && agent.GetProperty("max_tasks").GetInt32() == MaximumAgentTasks,
            "The definition must remain unelided and retain the selected concurrency.");
    }

    public static async Task StopAgentAsync(Ma2aViewModel target, NativeRuntimePair pair, string name, CancellationToken cancellationToken)
    {
        var before = await FindAgentAsync(target, name, cancellationToken);
        if (before.ValueKind == JsonValueKind.Undefined) return;
        GuiScenarios.SelectManagement(target, "agent_stop");
        Text(target, "agent", name);
        await ExecuteAsync(target, "agent_stopped", cancellationToken);
        var stopped = await FindAgentAsync(target, name, cancellationToken);
        NativeRuntimePair.Check(!stopped.GetProperty("enabled").GetBoolean() && (stopped.GetProperty("instance").ValueKind == JsonValueKind.Null
                || stopped.GetProperty("instance").GetProperty("ended_at_ms").ValueKind != JsonValueKind.Null), "GUI stop must disable and end the real Agent instance.");
        if (before.GetProperty("instance").ValueKind == JsonValueKind.Object && before.GetProperty("instance").GetProperty("process_id").GetString() is { } process)
        {
            GuiScenarios.SelectManagement(target, "process_inspect");
            Text(target, "process_id", process);
            await ExecuteAsync(target, "process", cancellationToken);
            var request = NativeRuntimePair.Command("process_inspect");
            request["process_id"] = process;
            var summary = NativeRuntimePair.Payload(await target.CreateClient().CallAsync(request, cancellationToken)).GetProperty("process");
            NativeRuntimePair.Check(summary.GetProperty("state").GetString() is "exited" or "terminated" && summary.GetProperty("ended_at_ms").ValueKind != JsonValueKind.Null,
                "GUI Agent stop must actually terminate its native Process, not only mark the Agent disabled.");
        }
        if (await HasExposureAsync(target, pair.SpaceId, "agent", name, cancellationToken))
        {
            GuiScenarios.SelectManagement(target, "service_unexpose");
            Text(target, "space_id", pair.SpaceId);
            Text(target, "kind", "agent");
            Text(target, "agent", name);
            await ExecuteAsync(target, "service_unexposed", cancellationToken);
        }
        GuiScenarios.SelectManagement(target, "agent_undefine");
        Text(target, "agent", name);
        await ExecuteAsync(target, "agent_undefined", cancellationToken);
        NativeRuntimePair.Check((await FindAgentAsync(target, name, cancellationToken)).ValueKind == JsonValueKind.Undefined, "Cleanup must remove only the GUI-created Agent definition.");
        await NativeRuntimePair.PassedAsync("GUI stop ends the authoritative SDK Agent instance, then removes only its exposure and definition.");
    }

    public static async Task RunAsync(Ma2aViewModel caller, Ma2aViewModel target, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        await ObserveConnectionsAsync(caller, cancellationToken);
        await ObserveConnectionsAsync(target, cancellationToken);
        using var owner = new Ma2aViewModel { StateDirectory = Path.Combine(pair.Root, "r"), Executable = pair.BinaryPath };
        using var member = new Ma2aViewModel { StateDirectory = Path.Combine(pair.Root, "s"), Executable = pair.BinaryPath };
        var started = new List<Ma2aViewModel>();
        try
        {
            started.Add(owner);
            await LifecycleAsync(owner, "start", cancellationToken);
            started.Add(member);
            await LifecycleAsync(member, "start", cancellationToken);
            var firstSpace = await JoinSpaceAsync(owner, member, pair.Root, cancellationToken);
            await ExposuresAsync(owner, member, firstSpace, pair.Root, cancellationToken);
            GuiScenarios.SelectManagement(member, "space_leave");
            Text(member, "space_id", firstSpace);
            await ExecuteAsync(member, "space_left", cancellationToken);
            NativeRuntimePair.Check(!(await ReadAsync(member, "space_list", cancellationToken)).EnumerateArray().Any(space => space.GetProperty("space_id").GetString() == firstSpace), "GUI leave must remove the caller's authoritative Space membership.");
            var secondSpace = await JoinSpaceAsync(owner, member, pair.Root, cancellationToken);
            await RevokeAsync(owner, member, secondSpace, cancellationToken);
            await RelaysAsync(owner, firstSpace, pair.Root, cancellationToken);
            await NativeRuntimePair.PassedAsync("GUI invitation redemption, explicit departure, membership revocation and exposure replacement run on isolated task-owned Runtimes.");
        }
        finally
        {
            await StopOwnedRuntimesAsync(started);
        }
    }

    private static async Task<string> JoinSpaceAsync(Ma2aViewModel owner, Ma2aViewModel member, string root, CancellationToken cancellationToken)
    {
        var name = $"gui-space-{Guid.NewGuid():N}";
        GuiScenarios.SelectManagement(owner, "space_create");
        Text(owner, "name", name);
        await ExecuteAsync(owner, "space_created", cancellationToken);
        var id = (await ReadAsync(owner, "space_list", cancellationToken)).EnumerateArray().Single(space => space.GetProperty("name").GetString() == name).GetProperty("space_id").GetString()!;
        var invitationPath = Path.Combine(root, $"gui-invitation-{Guid.NewGuid():N}");
        GuiScenarios.SelectManagement(owner, "space_invite");
        Text(owner, "space_id", id);
        Text(owner, "output_path", invitationPath);
        Field(owner, "ttl_ms").Number = InvitationLifetimeMilliseconds;
        await ExecuteAsync(owner, "space_invitation_created", cancellationToken);
        try
        {
            GuiScenarios.SelectManagement(member, "space_redeem");
            Text(member, "invitation", await File.ReadAllTextAsync(invitationPath, cancellationToken));
            await ExecuteAsync(member, "space_redeemed", cancellationToken);
        }
        finally { File.Delete(invitationPath); }
        GuiScenarios.SelectManagement(owner, "space_details_fetch");
        Text(owner, "space_id", id);
        await ExecuteAsync(owner, "space_details", cancellationToken);
        var members = (await ReadSpaceDetailsAsync(owner, id, cancellationToken)).GetProperty("members");
        NativeRuntimePair.Check(members.GetArrayLength() == 2 && members.EnumerateArray().Any(item => item.GetProperty("endpoint_id").GetString() == member.Endpoint), "GUI redemption must enroll the actual isolated caller in the intended Space.");
        NativeRuntimePair.Check(GuiScenarios.Labels(owner.Result).Contains($"endpoint id: {member.Endpoint}"), "The GUI member page must display the actual redeemed identity.");
        return id;
    }

    private static async Task ExposuresAsync(Ma2aViewModel owner, Ma2aViewModel member, string space, string root, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(owner, "space_grant");
        Text(owner, "space_id", space);
        Text(owner, "peer_endpoint_id", member.Endpoint);
        Field(owner, "files_read").Checked = true;
        await ExecuteAsync(owner, "space_granted", cancellationToken);
        GuiScenarios.SelectManagement(member, "control_sync_trigger");
        Text(member, "peer_endpoint_id", owner.Endpoint);
        await ExecuteAsync(member, "control_sync_triggered", cancellationToken);
        var name = $"gui-root-{Guid.NewGuid():N}";
        var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        GuiScenarios.SelectManagement(owner, "service_expose");
        Text(owner, "space_id", space);
        Text(owner, "kind", "files_root");
        Text(owner, "root", name);
        Text(owner, "path", path);
        Text(owner, "access", "read_only");
        await ExecuteAsync(owner, "service_exposed", cancellationToken);
        NativeRuntimePair.Check(await HasExposureAsync(owner, space, "files_root", name, cancellationToken), "GUI exposure must be visible in authoritative local exposure records.");
        await StatRootAsync(member, owner.Endpoint, name, cancellationToken);
        GuiScenarios.SelectManagement(owner, "service_unexpose");
        Text(owner, "space_id", space);
        Text(owner, "kind", "files_root");
        Text(owner, "root", name);
        await ExecuteAsync(owner, "service_unexposed", cancellationToken);
        NativeRuntimePair.Check(!await HasExposureAsync(owner, space, "files_root", name, cancellationToken), "GUI unexposure must remove its own root from authoritative records.");
        try
        {
            await StatRootAsync(member, owner.Endpoint, name, cancellationToken);
            throw new InvalidOperationException("The unexposed root remained authorized.");
        }
        catch (Ma2aServiceException error) when (error.Status == 3 && error.Rejected) { }
        await NativeRuntimePair.PassedAsync("GUI expose admits a real Files stat; GUI unexpose rejects the next stream with unauthorized while membership and grant remain.");
    }

    private static async Task RevokeAsync(Ma2aViewModel owner, Ma2aViewModel member, string space, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(owner, "space_revoke");
        Text(owner, "space_id", space);
        Text(owner, "peer_endpoint_id", member.Endpoint);
        await ExecuteAsync(owner, "space_revoked", cancellationToken);
        var members = (await ReadSpaceDetailsAsync(owner, space, cancellationToken)).GetProperty("members");
        NativeRuntimePair.Check(members.GetArrayLength() == 1 && members.EnumerateArray().All(item => item.GetProperty("endpoint_id").GetString() != member.Endpoint), "GUI revocation must remove the intended member from the owner's authority.");
        GuiScenarios.SelectManagement(member, "echo_call");
        Text(member, "target_endpoint_id", owner.Endpoint);
        Text(member, "payload", "GUI membership revocation acceptance");
        member.Confirmed = true;
        await member.ExecuteCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(member.HasPending && member.Status.Contains("unauthorized", StringComparison.Ordinal), "The actual GUI call after revocation must report unauthorized, not mere transport unavailability.");
    }

    private static async Task RelaysAsync(Ma2aViewModel owner, string space, string root, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(owner, "public_relay_configure");
        Text(owner, "url", PublicRelayUrl);
        await ExecuteAsync(owner, "public_relay_configured", cancellationToken);
        var configured = await ReadAsync(owner, "public_relay_status", cancellationToken);
        var expectedUrl = new Uri(PublicRelayUrl).AbsoluteUri;
        NativeRuntimePair.Check(configured.GetProperty("configured").GetBoolean() && configured.GetProperty("url").GetString() == expectedUrl,
            "Public relay GUI configuration must read back the canonical URL.");
        GuiScenarios.SelectManagement(owner, "public_relay_disable");
        await ExecuteAsync(owner, "public_relay_status", cancellationToken);
        NativeRuntimePair.Check(!(await ReadAsync(owner, "public_relay_status", cancellationToken)).GetProperty("configured").GetBoolean(), "Public relay GUI disable must be committed.");
        var certificates = await CreateRelayCertificateAsync(root, cancellationToken);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await PrivateRelayAsync(owner, space, port, certificates, cancellationToken);
        await NativeRuntimePair.PassedAsync("GUI configures and disables public relay state, then configures a real isolated TLS private relay and disables it without changing the main Runtime pair.");
    }

    private static async Task PrivateRelayAsync(Ma2aViewModel owner, string space, int port,
        (string Certificate, string Key, string Fingerprint) certificates, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(owner, "private_relay_configure");
        Text(owner, "mode", "native_tls");
        Text(owner, "listen", $"127.0.0.1:{port}");
        Text(owner, "public_url", $"https://localhost:{port}");
        GuiScenarios.AddArgument(Field(owner, "served_space_ids"), space);
        Text(owner, "certificate_path", certificates.Certificate);
        Text(owner, "private_key_path", certificates.Key);
        try
        {
            await ExecuteAsync(owner, "private_relay_configured", cancellationToken);
            var ready = await WaitForReadbackAsync(() => ReadAsync(owner, "private_relay_status", cancellationToken),
                value => value.GetProperty("online").GetBoolean(), "The isolated native TLS relay provider must actually become online.", cancellationToken);
            NativeRuntimePair.Check(ready.GetProperty("configured").GetBoolean() && ready.GetProperty("mode").GetString() == "native_tls" && ready.GetProperty("port").GetInt32() == port,
                "The private relay's authority must report the exact GUI-selected native TLS listener.");
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == certificates.Fingerprint,
            }, cancellationToken);
            NativeRuntimePair.Check(tls.IsAuthenticated && tls.IsEncrypted, "The configured provider must perform a real TLS handshake with its task-owned certificate.");
        }
        finally
        {
            try
            {
                GuiScenarios.SelectManagement(owner, "private_relay_disable");
                await ExecuteAsync(owner, "private_relay_status", CancellationToken.None);
            }
            finally
            {
                File.Delete(certificates.Certificate);
                File.Delete(certificates.Key);
            }
        }
        var disabled = await WaitForReadbackAsync(() => ReadAsync(owner, "private_relay_status", cancellationToken),
            value => !value.GetProperty("online").GetBoolean(), "GUI private relay disable must stop the isolated provider.", cancellationToken);
        NativeRuntimePair.Check(!disabled.GetProperty("configured").GetBoolean(), "Private relay disable must clear committed configuration.");
    }

    private static async Task<(string Certificate, string Key, string Fingerprint)> CreateRelayCertificateAsync(string root, CancellationToken cancellationToken)
    {
        using var key = RSA.Create(CertificateKeyBits);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var prefix = Path.Combine(root, $"gui-relay-{Guid.NewGuid():N}");
        await WritePrivateAsync($"{prefix}.crt", certificate.ExportCertificatePem(), cancellationToken);
        await WritePrivateAsync($"{prefix}.key", key.ExportPkcs8PrivateKeyPem(), cancellationToken);
        return ($"{prefix}.crt", $"{prefix}.key", certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    private static async Task WritePrivateAsync(string path, string content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static async Task ObserveConnectionsAsync(Ma2aViewModel model, CancellationToken cancellationToken)
    {
        var snapshot = NativeRuntimePair.Payload(await model.CreateClient().GetSnapshotAsync(cancellationToken));
        await model.RefreshCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(model.Connected, "The GUI must retain a live authoritative snapshot.");
        var actual = model.Snapshot.Single(node => node.Label == "connections");
        NativeRuntimePair.Check(snapshot.GetProperty("connections").GetArrayLength() > 0, "The real Runtime pair must supply actual connection observations for this GUI assertion.");
        foreach (var connection in snapshot.GetProperty("connections").EnumerateArray())
        {
            var identity = $"endpoint id: {connection.GetProperty("endpoint_id").GetString()}";
            var displayed = actual.Children.Single(node => GuiScenarios.Labels(node.Children).Contains(identity));
            var labels = GuiScenarios.Labels(displayed.Children);
            NativeRuntimePair.Check(labels.Contains($"state: {connection.GetProperty("state").GetString()}") && labels.Contains($"path: {connection.GetProperty("path").GetString()}"),
                "The GUI must display the actual per-connection state and path; no unobserved route class is invented.");
        }
        await NativeRuntimePair.PassedAsync("GUI renders only the connection paths and states actually observed in the authoritative Runtime snapshot.");
    }

    private static async Task StatRootAsync(Ma2aViewModel member, string endpoint, string root, CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(member.CreateClient(), endpoint, "files", Guid.NewGuid().ToString("N"), Ma2aServiceRequests.Stat($"{root}:/"), cancellationToken);
        while ((await session.ReadAsync(cancellationToken)).Kind != Ma2aFrameKind.Outcome) { }
    }

    private static async Task<bool> HasExposureAsync(Ma2aViewModel model, string space, string kind, string name, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(model, "service_exposure_list");
        await ExecuteAsync(model, "service_exposures", cancellationToken);
        var selector = kind == "agent" ? "agent" : "root";
        var exposed = (await ReadAsync(model, "service_exposure_list", cancellationToken)).EnumerateArray().Any(item =>
            item.GetProperty("space_id").GetString() == space && item.GetProperty("kind").GetString() == kind && item.GetProperty(selector).GetString() == name);
        NativeRuntimePair.Check(GuiScenarios.Labels(model.Result).Contains($"{selector}: {name}") == exposed, "GUI exposure listing must agree with an independent authoritative read.");
        return exposed;
    }

    private static async Task<JsonElement> FindAgentAsync(Ma2aViewModel model, string name, CancellationToken cancellationToken)
    {
        GuiScenarios.SelectManagement(model, "agent_list");
        await ExecuteAsync(model, "agents", cancellationToken);
        var request = NativeRuntimePair.Command("agent_list");
        request["after"] = null;
        var page = NativeRuntimePair.Payload(await model.CreateClient().CallAsync(request, cancellationToken));
        NativeRuntimePair.Check(page.GetProperty("next").ValueKind == JsonValueKind.Null, "The isolated acceptance Agent list must fit its actual bounded page.");
        var found = page.GetProperty("agents").EnumerateArray().SingleOrDefault(item => item.GetProperty("agent").GetString() == name);
        var visible = model.Result.Single(node => node.Label == "payload").Children.Single(node => node.Label == "agents").Children
            .SingleOrDefault(node => node.Children.Any(field => field.Label == $"agent: {name}"));
        NativeRuntimePair.Check((visible is not null) == (found.ValueKind == JsonValueKind.Object), "GUI Agent listing must agree with authoritative definition existence.");
        if (visible is not null)
            NativeRuntimePair.Check(GuiScenarios.Labels(visible.Children).Contains($"enabled: {found.GetProperty("enabled").GetBoolean()}"), "GUI Agent enabled state must agree with the native authority.");
        return found;
    }

    private static async Task<JsonElement> ReadSpaceDetailsAsync(Ma2aViewModel model, string id, CancellationToken cancellationToken)
    {
        var request = NativeRuntimePair.Command("space_details_fetch");
        request["space_id"] = id;
        return NativeRuntimePair.Payload(await model.CreateClient().CallAsync(request, cancellationToken));
    }

    private static async Task<JsonElement> ReadAsync(Ma2aViewModel model, string operation, CancellationToken cancellationToken)
        => NativeRuntimePair.Payload(await model.CreateClient().CallAsync(NativeRuntimePair.Command(operation), cancellationToken));

    private static bool AgentReady(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.GetProperty("enabled").GetBoolean()
        && value.GetProperty("instance").ValueKind == JsonValueKind.Object && value.GetProperty("instance").GetProperty("ready").GetBoolean()
        && value.GetProperty("instance").GetProperty("process_id").ValueKind == JsonValueKind.String
        && value.GetProperty("instance").GetProperty("ended_at_ms").ValueKind == JsonValueKind.Null;

    private static async Task<JsonElement> WaitForReadbackAsync(Func<Task<JsonElement>> read, Func<JsonElement, bool> matches, string failure, CancellationToken cancellationToken)
    {
        var pending = read();
        JsonElement value = default;
        await GuiScenarios.UntilAsync(() =>
        {
            if (!pending.IsCompleted) return false;
            value = pending.GetAwaiter().GetResult();
            if (matches(value)) return true;
            pending = read();
            return false;
        }, failure, cancellationToken);
        return value;
    }

    private static Ma2aFieldViewModel Field(Ma2aViewModel model, string name) => model.Fields.Single(field => field.Name == name);

    private static void Text(Ma2aViewModel model, string name, string value)
    {
        var field = Field(model, name);
        if (field.IsNullable) field.IsNull = false;
        field.Text = value;
    }

    private static async Task ExecuteAsync(Ma2aViewModel model, string expected, CancellationToken cancellationToken)
    {
        var operation = model.SelectedCommand!.Operation;
        model.Confirmed = true;
        await model.ExecuteCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(!model.HasPending && GuiScenarios.Labels(model.Result).Contains($"type: {expected}"), $"GUI {operation} must return native result {expected}; private response details were withheld.");
    }

    private static async Task LifecycleAsync(Ma2aViewModel model, string operation, CancellationToken cancellationToken)
    {
        model.LifecycleConfirmed = true;
        await model.LifecycleCommand.ExecuteAsync(operation).WaitAsync(cancellationToken);
        NativeRuntimePair.Check(model.Connected == (operation != "stop"), "The isolated GUI Runtime lifecycle must reach the requested state.");
        if (operation != "stop") return;
        NativeRuntimePair.Check(model.Status == "Runtime 已停止。", "The isolated Runtime must confirm its explicit stop command.");
        try
        {
            await ReadAsync(model, "status", cancellationToken);
            throw new InvalidOperationException("A task-owned Runtime remained reachable after GUI stop.");
        }
        catch (Exception error) when (error is SocketException || error is IOException and not Ma2aProtocolException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
    }

    private static async Task StopOwnedRuntimesAsync(IReadOnlyList<Ma2aViewModel> started)
    {
        var errors = new List<Exception>();
        foreach (var model in started.Reverse())
        {
            try { await LifecycleAsync(model, "stop", CancellationToken.None); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Isolated GUI Runtime cleanup failed.", errors);
        await NativeRuntimePair.PassedAsync("Both isolated GUI management Runtimes are explicitly stopped and their native status endpoints are unreachable.");
    }
}
