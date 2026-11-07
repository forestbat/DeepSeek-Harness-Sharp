using System.Text;
using Dsh.Boot;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Plugins.Native;
using Dsh.Plugins.Native.Host;
using Dsh.Plugins.Sandbox;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 原生 ABI 端到端回归: 宿主装载 NativeAOT 发布的 Dsh.NativePluginSample, 经真实的生成胶水调用工具。
 * 样例需先发布: dotnet publish tests/Dsh.NativePluginSample -c Release; 未发布或非 Linux 时跳过。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class NativePluginAbiTests
{
    private const string EchoTool = "native_echo";
    private const string LengthTool = "native_length";
    private const int LargeMessageLength = 200_000;
    // All payloads in these binary compatibility regressions are synthetic test inputs.


    [Fact]
    public async Task PublishedSample_LifecyclePreservesConfiguration_ToolFailures_AndReload()
    {
        var sample = PublishedSamplePath();
        for (var activation = 0; activation < 2; activation++)
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            var load = NativePluginLibrary.TryLoad(sample);
            Assert.True(load.Succeeded, load.Reason);
            var configuration = activation == 0
                ? new Dictionary<string, object?> { ["label"] = "config 中文", ["enabled"] = true }
                : null;
            using (var native = load.Plugin!)
            using (var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(native)
                .Apply(ctx, configuration, TestContext.Current.CancellationToken))!)
            {
                var response = await ExecuteJson(tools, "native_config", JsonSerializer.SerializeToElement(new { }));
                if (configuration is null)
                    Assert.Equal("", response.GetProperty("config").GetString());
                else
                {
                    using var configured = JsonDocument.Parse(response.GetProperty("config").GetString()!);
                    Assert.Equal("config 中文", configured.RootElement.GetProperty("label").GetString());
                    Assert.True(configured.RootElement.GetProperty("enabled").GetBoolean());
                    var message = $"大消息-{new string('x', LargeMessageLength)}";
                    var result = await tools.Execute(new ToolExecutionInput
                    {
                        CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                        Name = EchoTool,
                        Arguments = JsonSerializer.SerializeToElement(new { message }),
                        Agent = null,
                        Signal = TestContext.Current.CancellationToken,
                    });
                    Assert.False(result.IsError);
                    Assert.Equal($"echo:{message}", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
                    Assert.Throws<NullReferenceException>(() => LengthOf(null));
                    var failed = await tools.Execute(new ToolExecutionInput
                    {
                        CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                        Name = LengthTool,
                        Arguments = JsonSerializer.SerializeToElement(new { }),
                        Agent = null,
                        Signal = TestContext.Current.CancellationToken,
                    });
                    Assert.True(failed.IsError);
                    Assert.Equal(NativePluginState.Active, native.State);
                    Assert.NotNull(tools.Get(EchoTool));
                }
            }
            Assert.Null(tools.Get("native_config"));
            Assert.Null(tools.Get("native_http"));
            Assert.Null(tools.Get(EchoTool));
            Assert.Throws<NullReferenceException>(() => LengthOf(null));
        }
    }


    [Theory]
    [Trait("Category", "SandboxNative")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishedSample_IsolatedWorkerUsesBrokerPermissionsAndNeverRunsInMain(bool write)
    {
        var sample = PublishedSamplePath();
        using var fixture = new PluginSandboxIntegrationTests.Fixture(settings =>
            settings.Plugins["sample.native-echo"] = new PluginSetting
            {
                Execution = PluginExecution.Sandbox,
                Permissions = new PluginPermissions { Read = true, Write = write, Network = false },
            }, deployFixture: false);
        var directory = Path.Combine(fixture.Plugins, "native-sdk");
        Directory.CreateDirectory(directory);
        File.Copy(sample, Path.Combine(directory, Path.GetFileName(sample)));
        var input = Path.Combine(fixture.Root, "native-input");
        await File.WriteAllTextAsync(input, "native 输入", TestContext.Current.CancellationToken);
        await fixture.StartAsync(expectFixtureActive: false);
        var config = Assert.IsType<ToolExecutionResult.Success>(await fixture.ExecuteTool("native_config", new { }, TestContext.Current.CancellationToken)).Value;
        var workerNamespace = config.GetProperty("pidNamespace").GetString();
        Assert.StartsWith("pid:[", workerNamespace);
        Assert.NotEqual(new FileInfo("/proc/self/ns/pid").LinkTarget, workerNamespace);
        var read = Assert.IsType<ToolExecutionResult.Success>(await fixture.ExecuteTool("native_read_file", new { path = input }, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("native 输入")), read.GetProperty("data").GetString());
        var output = Path.Combine(fixture.Root, "native-output");
        var result = await fixture.ExecuteTool("native_write_file", new { path = output, data = Convert.ToBase64String(Encoding.UTF8.GetBytes("native 输出")) }, TestContext.Current.CancellationToken);
        Assert.Equal(!write, result.IsError);
        Assert.Equal(write, File.Exists(output));
        if (write)
            Assert.Equal("native 输出", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishedSample_FileAndHttpBrokerCallsExecuteOnce_AndRecoverFromOperationFailure()
    {
        var root = TempTree.CreateDirectory("native-common-broker");
        try
        {
            await using var server = await PluginSdkHttpServer.StartAsync();
            var ctx = CreateCapabilityContext(root, out var tools);
            var load = NativePluginLibrary.TryLoad(PublishedSamplePath());
            Assert.True(load.Succeeded, load.Reason);
            using var native = load.Plugin!;
            using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(native)
                .Apply(ctx, null, TestContext.Current.CancellationToken))!;
            var path = Path.Combine(root, "payload.txt");
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes("native payload"));
            var written = await ExecuteJson(tools, "native_write_file", JsonSerializer.SerializeToElement(new { path, data, append = true }));
            Assert.Equal(14, written.GetProperty("bytesWritten").GetInt32());
            Assert.Equal("native payload", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var read = await ExecuteJson(tools, "native_read_file", JsonSerializer.SerializeToElement(new { path }));
            Assert.Equal(data, read.GetProperty("data").GetString());
            Assert.True(read.GetProperty("eof").GetBoolean());
            var failed = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "native_read_file",
                Arguments = JsonSerializer.SerializeToElement(new { path = Path.Combine(root, "missing.txt") }),
                Agent = null,
                Signal = TestContext.Current.CancellationToken,
            });
            Assert.True(failed.IsError);
            Assert.Equal(NativePluginState.Active, native.State);
            var body = Encoding.UTF8.GetBytes("native request");
            var response = await ExecuteJson(tools, "native_http", JsonSerializer.SerializeToElement(new
            {
                url = server.Url,
                method = "POST",
                headers = new Dictionary<string, string> { ["X-Plugin"] = "sdk" },
                body = Convert.ToBase64String(body),
            }));
            Assert.Equal(201, response.GetProperty("status").GetInt32());
            Assert.Equal(server.ResponseBody, Convert.FromBase64String(response.GetProperty("body").GetString()!));
            Assert.Equal(body, server.RequestBody);
            Assert.Equal("sdk", server.RequestHeader);
            Assert.Equal(1, server.Requests);
        }
        finally { TempTree.Delete(root); }
    }

    private static Context CreateCapabilityContext(string root, out ToolRuntime tools)
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        tools = new ToolRuntime(ctx);
        var settings = new HarnessSettings { Safety = new SafetySettings { AutoApprove = true } };
        _ = new SafetyBoundary(ctx, () => settings);
        ctx.Provide(IPluginHostFactory.ServiceName,
            new PluginCapabilityFactory(ctx, new PluginExecutionEnvironment(new HarnessHome(root), settings, root)));
        return ctx;
    }

    private static async Task<JsonElement> ExecuteJson(ToolRuntime tools, string name, JsonElement arguments)
    {
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = arguments,
            Agent = null,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(result.IsError);
        using var document = JsonDocument.Parse(string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        return document.RootElement.Clone();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LengthOf(string? text) => text!.Length;

    private static string PublishedSamplePath()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("样例只发布 linux-x64 原生库");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "Dsh.NativePluginSample", "bin", "Release", "net10.0", "linux-x64", "publish", "Dsh.NativePluginSample.so");
        if (!File.Exists(path))
            Assert.Skip($"未发布原生样例({path}), 先运行 dotnet publish tests/Dsh.NativePluginSample -c Release");
        return path;
    }
}
