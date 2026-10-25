using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Mcp;
using Dsh.Runtime;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dsh.Tests.Mcp;

/** Connects one McpService to two real child processes of the test-only Dsh.McpResourceServerSample: "res" serves resources, "plain" serves only a tool. */
public sealed class McpResourceServerFixture : IAsyncLifetime
{
    public const string ResourceServer = "res";
    public const string PlainServer = "plain";

    private const string SampleDll = "Dsh.McpResourceServerSample.dll";
    private const string NoResourcesFlag = "--no-resources";

    public Context Ctx { get; } = new();

    public string Home { get; } = TempTree.CreateDirectory("mcp-resources");

    public string[] ServerArguments { get; init; } = [];

    public ToolRuntime Tools { get; private set; } = null!;

    public SystemPrompt Prompt { get; private set; } = null!;

    public McpService Service { get; private set; } = null!;

    /** Launches the sample through the dotnet host that runs this test process, so no apphost or PATH lookup is involved. */
    public static McpServerConfig ServerConfig(string name, bool withResources = true, params string[] arguments)
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var muxer = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var dll = Path.Combine(AppContext.BaseDirectory, "mcp-resource-server", SampleDll);
        return new McpServerConfig(name, "stdio", muxer, withResources ? [dll, .. arguments] : [dll, NoResourcesFlag, .. arguments]);
    }

    public static async Task ConnectAsync(McpService service, params McpServerConfig[] servers)
    {
        foreach (var server in servers)
            await service.ConnectAsync(server, () => new ReadyStdioTransport(server), TestContext.Current.CancellationToken);
    }

    private sealed class ReadyStdioTransport(McpServerConfig server) : IClientTransport
    {
        private const string ReadyMessage = "MCP_RESOURCE_SAMPLE_READY";

        public string Name => server.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdio = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command!,
                Arguments = [.. server.Arguments ?? []],
                StandardErrorLines = line =>
                {
                    if (line == ReadyMessage)
                        ready.TrySetResult();
                },
            });
            var transport = await stdio.ConnectAsync(cancellationToken);
            try
            {
                await Task.WhenAny(ready.Task, transport.MessageReader.Completion).WaitAsync(cancellationToken);
                if (!ready.Task.IsCompletedSuccessfully)
                {
                    await transport.MessageReader.Completion;
                    throw new IOException("MCP resource sample exited before its message loop was ready.");
                }
                return transport;
            }
            catch
            {
                await transport.DisposeAsync();
                throw;
            }
        }
    }

    public async ValueTask InitializeAsync()
    {
        Ctx.SetOwn("dshHomePath", Home);
        Prompt = new SystemPrompt(Ctx, new SystemPromptConfig());
        Tools = new ToolRuntime(Ctx);
        _ = new FileAttachmentStore(Ctx);
        Service = new McpService(Ctx);
        await ConnectAsync(Service, ServerConfig(ResourceServer, true, ServerArguments), ServerConfig(PlainServer, withResources: false));
    }

    public Task<ToolExecutionResult> Execute(string name, string arguments)
        => Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

    public async Task<string> SectionText()
    {
        var assembly = await Prompt.Assemble(new AssembleContext());
        return assembly.Sections.Single(section => section.Name == McpResourceTools.SectionName).Text;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        TempTree.Delete(Home);
    }
}

[Collection(SerialProcessCollection.Name)]
public sealed class McpResourceToolsTests(McpResourceServerFixture fixture) : IClassFixture<McpResourceServerFixture>
{
    private const string GuideText = "# Guide\nResource text served by the sample MCP server.";
    private const string LogoPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    private static readonly TimeSpan NotificationDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private static string TextOf(ToolExecutionResult result)
        => string.Join('\n', result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static void AssertSucceeded(ToolExecutionResult result)
        => Assert.False(result.IsError, result is ToolExecutionResult.Failure failure ? failure.Error.Message : null);

    [Fact]
    public void Status_ReportsWhichServersDeclareResources()
    {
        var statuses = fixture.Service.Status().ToDictionary(status => status.Name);

        Assert.True(statuses[McpResourceServerFixture.ResourceServer].Connected, statuses[McpResourceServerFixture.ResourceServer].Error);
        Assert.True(statuses[McpResourceServerFixture.ResourceServer].Resources);
        Assert.True(statuses[McpResourceServerFixture.PlainServer].Connected, statuses[McpResourceServerFixture.PlainServer].Error);
        Assert.False(statuses[McpResourceServerFixture.PlainServer].Resources);
        Assert.Contains("res: stdio connected (1 tools registered, resources)", McpCommand.Format(fixture.Service.Status()));
    }

    [Fact]
    public async Task ListResources_ReturnsTheServersResourcesAttributedToIt()
    {
        var result = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }""");

        AssertSucceeded(result);
        var text = TextOf(result);
        Assert.StartsWith("MCP server: res\n", text);
        Assert.Contains("docs://guide", text);
        Assert.Contains("files://logo.png", text);
        Assert.Contains("files://data.bin", text);
        Assert.Contains("text/markdown", text);
    }

    [Fact]
    public async Task ListResourceTemplates_ReturnsUriTemplates()
    {
        var result = await fixture.Execute(McpResourceTools.ListTemplatesName, """{ "server": "res" }""");

        AssertSucceeded(result);
        Assert.Contains("notes://{name}", TextOf(result));
        Assert.Contains("archive://{name}", TextOf(result));
    }

    [Fact]
    public async Task ListingCursor_ReturnsOnlyOnePageAndPreservesTheNextCursor()
    {
        var resources = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res", "cursor": "0" }""");
        var resourcePage = Assert.IsType<ToolExecutionResult.Success>(resources).Value;
        Assert.Equal(2, resourcePage.GetProperty("resources").GetArrayLength());
        Assert.Equal("2", resourcePage.GetProperty("nextCursor").GetString());
        var templates = await fixture.Execute(McpResourceTools.ListTemplatesName, """{ "server": "res", "cursor": "0" }""");
        var templatePage = Assert.IsType<ToolExecutionResult.Success>(templates).Value;
        Assert.Equal(1, templatePage.GetProperty("resourceTemplates").GetArrayLength());
        Assert.Equal("1", templatePage.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task ReadResource_ReturnsTextDirectly()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "docs://guide" }""");

        AssertSucceeded(result);
        var block = Assert.IsType<TextBlock>(Assert.Single(result.Content));
        Assert.Equal($"MCP resource docs://guide from server \"res\" (text/markdown):\n{GuideText}", block.Text);
    }

    [Fact]
    public async Task ReadResource_ExpandsATemplateUri()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "notes://alpha" }""");

        AssertSucceeded(result);
        Assert.EndsWith("note:alpha", TextOf(result));
    }

    [Fact]
    public async Task ReadResource_StoresAnImageBlobAsAContentAddressedAttachment()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "files://logo.png" }""");

        AssertSucceeded(result);
        var content = Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("contents").EnumerateArray());
        Assert.False(content.TryGetProperty("blob", out _));
        var expected = Convert.FromBase64String(LogoPngBase64);
        var path = content.GetProperty("path").GetString()!;
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var store = fixture.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName)!;
        Assert.True(store.TryRead(content.GetProperty("attachmentId").GetString()!, out var stored));
        Assert.Equal(expected, stored);
        var text = TextOf(result);
        Assert.Contains($"{expected.Length} bytes saved as content-addressed attachment", text);
        Assert.Contains(path, text);
        Assert.Contains("read_image", text);
    }

    [Fact]
    public async Task ReadResource_StoresANonImageBlobWithoutTheImageHint()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "res", "uri": "files://data.bin" }""");

        AssertSucceeded(result);
        var content = Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("contents").EnumerateArray());
        var bytes = await File.ReadAllBytesAsync(content.GetProperty("path").GetString()!, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 256).Select(value => (byte)value), bytes);
        Assert.Contains("(application/octet-stream) is binary: 256 bytes", TextOf(result));
        Assert.DoesNotContain("read_image", TextOf(result));
    }

    [Fact]
    public async Task ServerWithoutResourcesCapability_FailsWithAnExplicitMessage()
    {
        var result = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "plain" }""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(McpServerHandle.ResourcesUnsupportedCode, failure.Error.Info?.Code);
        Assert.Contains("does not declare the resources capability", failure.Error.Message);
        Assert.Contains("MCP servers without resources (the resource tools fail for them): plain.", await fixture.SectionText());
    }

    [Fact]
    public async Task UnknownServer_ListsTheConfiguredServers()
    {
        var result = await fixture.Execute(McpResourceTools.ReadResourceName, """{ "server": "nope", "uri": "docs://guide" }""");

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(McpResourceTools.UnknownServerCode, failure.Error.Info?.Code);
        Assert.Contains("configured servers: res, plain", failure.Error.Message);
    }

    [Fact]
    public async Task ResourceListChanged_PromptsARelistUntilTheModelListsAgain()
    {
        var uri = $"docs://added-{Guid.NewGuid():N}";
        AssertSucceeded(await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }"""));
        Assert.DoesNotContain("resource list changed", await fixture.SectionText());

        AssertSucceeded(await fixture.Execute("mcp_res_add_resource", $$"""{ "uri": "{{uri}}", "text": "fresh" }"""));
        var deadline = DateTime.UtcNow + NotificationDeadline;
        while (!(await fixture.SectionText()).Contains("resource list changed on res") && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);

        Assert.Contains("The resource list changed on res after your last listing", await fixture.SectionText());
        var relisted = await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }""");
        AssertSucceeded(relisted);
        Assert.Contains(uri, TextOf(relisted));
        Assert.DoesNotContain("resource list changed", await fixture.SectionText());
        Assert.Contains("MCP servers with resources: res.", await fixture.SectionText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_RequiresIndependentFullResourceAndTemplateListings(bool templatesFirst)
    {
        var uri = $"docs://independent-{Guid.NewGuid():N}";
        var count = fixture.Service.Status().Single(status => status.Name == "res").ResourceListChanges;
        AssertSucceeded(await fixture.Execute("mcp_res_add_resource", $$"""{ "uri": "{{uri}}", "text": "fresh" }"""));
        var deadline = DateTime.UtcNow + NotificationDeadline;
        while (fixture.Service.Status().Single(status => status.Name == "res").ResourceListChanges <= count && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        Assert.Contains("resource list changed on res", await fixture.SectionText());
        Assert.Contains("resource template list changed on res", await fixture.SectionText());

        AssertSucceeded(await fixture.Execute(McpResourceTools.ListResourcesName, """{ "server": "res", "cursor": "2" }"""));
        AssertSucceeded(await fixture.Execute(McpResourceTools.ListTemplatesName, """{ "server": "res", "cursor": "1" }"""));
        Assert.Contains("resource list changed on res", await fixture.SectionText());
        Assert.Contains("resource template list changed on res", await fixture.SectionText());

        var first = templatesFirst ? McpResourceTools.ListTemplatesName : McpResourceTools.ListResourcesName;
        var second = templatesFirst ? McpResourceTools.ListResourcesName : McpResourceTools.ListTemplatesName;
        AssertSucceeded(await fixture.Execute(first, """{ "server": "res" }"""));
        var section = await fixture.SectionText();
        Assert.Contains(templatesFirst ? "resource list changed on res" : "resource template list changed on res", section);
        Assert.DoesNotContain(templatesFirst ? "resource template list changed on res" : "resource list changed on res", section);
        AssertSucceeded(await fixture.Execute(second, """{ "server": "res" }"""));
        Assert.DoesNotContain("list changed", await fixture.SectionText());
    }

    [Fact]
    public async Task LegacyProtocol_DeliversListChangedWithoutAListenRequest()
    {
        await using var legacy = new McpResourceServerFixture { ServerArguments = ["--legacy"] };
        await legacy.InitializeAsync();
        Assert.Equal("2025-11-25", await legacy.Service.Handles().Single(handle => handle.Config.Name == McpResourceServerFixture.ResourceServer)
            .CallResourcesAsync(client => ValueTask.FromResult(client.NegotiatedProtocolVersion), TestContext.Current.CancellationToken));
        AssertSucceeded(await legacy.Execute("mcp_res_add_resource", """{ "uri": "docs://legacy-added", "text": "legacy" }"""));
        var deadline = DateTime.UtcNow + NotificationDeadline;
        while (!(await legacy.SectionText()).Contains("resource list changed on res") && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        Assert.Contains("resource list changed on res", await legacy.SectionText());
        Assert.Null(legacy.Service.Status().Single(status => status.Name == "res").Error);
    }

    [Theory]
    [InlineData("--listen-fails", false)]
    [InlineData("--listen-ends", false)]
    [InlineData("--listen-fails", true)]
    [InlineData("--listen-ends", true)]
    public async Task NotificationStreamEndingOnALiveConnection_IsVisibleAndReconnectsOnTheNextCall(string mode, bool useTool)
    {
        var markerHome = TempTree.CreateDirectory("mcp-listen");
        try
        {
            await using var ending = new McpResourceServerFixture { ServerArguments = [mode, Path.Combine(markerHome, "ended")] };
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Experimental.ModelContextProtocol",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    ending.Ctx.LoggerFor("mcp-fixture").Info(
                        $"{activity.Kind} {activity.OperationName}: {activity.Status} {activity.StatusDescription}; {activity.Duration.TotalMilliseconds} ms; {string.Join(", ", activity.Tags)}; {string.Join("; ", activity.Events.Select(item => $"{item.Name}: {string.Join(", ", item.Tags)}"))}");
                },
            };
            ActivitySource.AddActivityListener(listener);
            await ending.InitializeAsync();
            var endListen = await ending.Execute("mcp_res_end_listen", "{}");
            if (endListen.IsError)
                Assert.Fail($"""
                    {(endListen is ToolExecutionResult.Failure failure ? failure.Error.Message : TextOf(endListen))}
                    MCP status: {JsonSerializer.Serialize(ending.Service.Status())}
                    Registered tools: {string.Join(", ", ending.Tools.Schemas().Select(schema => schema.Name))}
                    SDK/runtime log:
                    {string.Join('\n', ending.Ctx.Logger.Buffer.Select(message => $"{message.Name}: {message.Text}"))}
                    """);
            var deadline = DateTime.UtcNow + NotificationDeadline;
            while (ending.Service.Status().Single(status => status.Name == "res").Error is null && DateTime.UtcNow < deadline)
                await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
            var status = ending.Service.Status().Single(status => status.Name == "res");
            Assert.True(status.Connected);
            Assert.Contains("notification stream", status.Error);
            Assert.Contains("Resource change notifications are unavailable on res", await ending.SectionText());

            AssertSucceeded(await ending.Execute(useTool ? "mcp_res_add_resource" : McpResourceTools.ReadResourceName,
                useTool ? """{ "uri": "docs://reconnected", "text": "fresh" }""" : """{ "server": "res", "uri": "docs://guide" }"""));
            Assert.Null(ending.Service.Status().Single(status => status.Name == "res").Error);
            var beforeNotification = ending.Service.Status().Single(status => status.Name == "res").ResourceListChanges;
            AssertSucceeded(await ending.Execute("mcp_res_add_resource", """{ "uri": "docs://after-reconnect", "text": "fresh" }"""));
            deadline = DateTime.UtcNow + NotificationDeadline;
            while (ending.Service.Status().Single(status => status.Name == "res").ResourceListChanges <= beforeNotification && DateTime.UtcNow < deadline)
                await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
            Assert.True(ending.Service.Status().Single(status => status.Name == "res").ResourceListChanges > beforeNotification);
            AssertSucceeded(await ending.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }"""));
            Assert.Contains("resource template list changed on res", await ending.SectionText());
        }
        finally
        {
            TempTree.Delete(markerHome);
        }
    }

    [Fact]
    public async Task NotificationSubscriptionDenied_IsReportedWhileToolsRemainConnected()
    {
        await using var denied = new McpResourceServerFixture { ServerArguments = ["--listen-denied", Path.Combine(fixture.Home, "unused-marker")] };
        await denied.InitializeAsync();
        Assert.True(denied.Service.Status().Single(status => status.Name == "res").Connected);
        Assert.Contains("did not grant", denied.Service.Status().Single(status => status.Name == "res").Error);
        Assert.Contains("Resource change notifications are unavailable on res", await denied.SectionText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationDuringListing_IsNotConsumedEvenWhenTheListingFails(bool fail)
    {
        var marker = Guid.NewGuid().ToString("N");
        using var scenario = new Activity(nameof(NotificationDuringListing_IsNotConsumedEvenWhenTheListingFails))
            .AddBaggage("test.mcp.listing", marker).Start();
        string? connectionId = null;
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Experimental.ModelContextProtocol",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var session = activity.GetTagItem("mcp.session.id") as string;
                if (activity.Kind == ActivityKind.Client && activity.OperationName == RequestMethods.ResourcesList
                    && activity.GetBaggageItem("test.mcp.listing") == marker)
                    Volatile.Write(ref connectionId, session);
                if (activity.Kind == ActivityKind.Server && activity.OperationName == NotificationMethods.ResourceListChangedNotification
                    && session is not null && session == Volatile.Read(ref connectionId))
                    consumed.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var racing = new McpResourceServerFixture { ServerArguments = ["--listing-race"] };
        await racing.InitializeAsync();
        Assert.Equal("2026-07-28", await racing.Service.Handles().Single(handle => handle.Config.Name == McpResourceServerFixture.ResourceServer)
            .CallResourcesAsync(client => ValueTask.FromResult(client.NegotiatedProtocolVersion), TestContext.Current.CancellationToken));
        AssertSucceeded(await racing.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }"""));
        Assert.NotNull(Volatile.Read(ref connectionId));
        AssertSucceeded(await racing.Execute("mcp_res_change_during_listing", fail ? """{ "fail": true }""" : """{ "fail": false }"""));
        var listing = await racing.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }""");
        Assert.Equal(fail, listing.IsError);
        // The SDK dispatches responses independently; its incoming span stops after all notification handlers complete.
        await consumed.Task.WaitAsync(NotificationDeadline, TestContext.Current.CancellationToken);
        Assert.Contains("resource list changed on res", await racing.SectionText());
        Assert.Contains("resource template list changed on res", await racing.SectionText());
        AssertSucceeded(await racing.Execute(McpResourceTools.ListResourcesName, """{ "server": "res" }"""));
        Assert.DoesNotContain("resource list changed on res", await racing.SectionText());
        Assert.Contains("resource template list changed on res", await racing.SectionText());
    }
}
