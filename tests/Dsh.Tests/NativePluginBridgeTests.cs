using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Plugins.Native;
using Dsh.Plugins.Native.Host;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class NativePluginBridgeTests
{
    [Fact]
    public async Task RegisteredTool_InvokesPluginAndRendersText()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin();

        var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, null, TestContext.Current.CancellationToken))!;

        Assert.NotNull(tools.Get("native_echo"));
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "native_echo",
            Arguments = JsonDocument.Parse("""{"message":"hi"}""").RootElement,
            Agent = null,
            Signal = default,
        });
        Assert.False(result.IsError);
        Assert.Contains("echo:hi", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Contains(ctx.Root.Logger.Buffer, message => message.Text.Contains("native plugin \"test.native\""));

        registration.Dispose();
        Assert.True(plugin.Deactivated);
        Assert.Null(tools.Get("native_echo"));
    }

    [Fact]
    public void CatalogDefinition_ExposesNativePackage()
    {
        var catalog = new PluginCatalog();
        var plugin = new FakeNativePlugin();
        catalog.Register(
            PluginDescriptor.For(plugin.Package, PluginForm.NativeLibrary),
            () => NativePluginBridge.AsPlugin(plugin));

        Assert.Contains(plugin.Package, catalog.PackageNames);
        Assert.True(catalog.TryCreateDefinition(plugin.Package, out var definition));
        Assert.Equal(plugin.Package, definition!.Name);
        Assert.True(catalog.TryGet(plugin.Package, out var create));
        Assert.Empty(create().Inject);
    }

    /** 原生 ABI 的配置通道: 宿主把 plugins.<包名> 段编成 JSON, 激活前交给插件(旧插件不取即无感)。 */
    [Fact]
    public async Task Activation_Passes_Plugin_Settings_As_Json()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin();

        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timeoutMs"] = 2500L,
            ["enabled"] = true,
            ["label"] = "hi \"there\"",
            ["tags"] = new List<object?> { "a", 2L },
        }, TestContext.Current.CancellationToken))!;

        Assert.Equal("""{"timeoutMs":2500,"enabled":true,"label":"hi \"there\"","tags":["a",2]}""", plugin.ConfigJson);
    }

    /** 没有配置段时交空串(插件按默认值走), 而不是 "null" 或 "{}"。 */
    [Fact]
    public async Task Activation_Without_Settings_Passes_Empty_Config()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin();

        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, null, TestContext.Current.CancellationToken))!;

        Assert.Equal("", plugin.ConfigJson);
    }

    [Fact]
    public async Task RuntimeUnregisterTool_RemovesFromToolRuntime()    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin();
        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, null, TestContext.Current.CancellationToken))!;
        Assert.NotNull(tools.Get("native_echo"));

        Assert.True(plugin.RemoveRegisteredTool());

        Assert.Null(tools.Get("native_echo"));
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "native_echo",
            Arguments = JsonDocument.Parse("""{"message":"hi"}""").RootElement,
            Agent = null,
            Signal = default,
        });
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task InvokeFault_MarksFaultedRemovesToolsAndRefusesReuse()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin { Faulting = true };
        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, null, TestContext.Current.CancellationToken))!;

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "native_echo",
            Arguments = JsonDocument.Parse("""{"message":"hi"}""").RootElement,
            Agent = null,
            Signal = default,
        });

        Assert.True(result.IsError);
        Assert.Equal(NativePluginState.Faulted, plugin.State);
        Assert.Null(tools.Get("native_echo"));
        Assert.Contains(ctx.Root.Logger.Buffer, message => message.Text.Contains("invoke failed"));
    }

    /** 插件给出无效结果只是这次工具调用出错, 不是原生故障: 插件保持可用, 工具不被移除。 */
    [Fact]
    public async Task InvalidToolResult_IsToolError_WithoutFaultingPlugin()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var plugin = new FakeNativePlugin { ReturnsInvalidJson = true };
        using var registration = (IDisposable)(await NativePluginBridge.CreateDefinition(plugin).Apply(ctx, null, TestContext.Current.CancellationToken))!;

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = "native_echo",
            Arguments = JsonDocument.Parse("""{"message":"hi"}""").RootElement,
            Agent = null,
            Signal = default,
        });

        Assert.True(result.IsError);
        Assert.Equal(NativePluginState.Active, plugin.State);
        Assert.NotNull(tools.Get("native_echo"));
    }

    [Fact]
    public void TryLoad_NonNativeFile_ReportsStructuredFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-not-native-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "this is not a native shared library");
        try
        {
            var load = NativePluginLibrary.TryLoad(path);

            Assert.False(load.Succeeded);
            Assert.Equal(NativePluginFailureKind.NotALibrary, load.Failure);
            Assert.False(string.IsNullOrWhiteSpace(load.Reason));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_QuarantinedFile_ReportsQuarantineBeforeLoading()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"dsh-quarantine-{Guid.NewGuid():N}.dll"));
        File.WriteAllText(path, "content is irrelevant: quarantine is checked before load");
        try
        {
            NativePluginQuarantine.Default.RecordFault(path);
            NativePluginQuarantine.Default.RecordFault(path);

            var load = NativePluginLibrary.TryLoad(path);

            Assert.False(load.Succeeded);
            Assert.Equal(NativePluginFailureKind.Quarantined, load.Failure);
        }
        finally
        {
            NativePluginQuarantine.Default.Reset(path);
            File.Delete(path);
        }
    }

    [Fact]
    public void Quarantine_BansOnlyAfterSecondFault()
    {
        var key = $"dsh-quarantine-key-{Guid.NewGuid():N}";
        var quarantine = new NativePluginQuarantine();
        try
        {
            Assert.False(quarantine.IsBanned(key));
            Assert.Equal(1, quarantine.RecordFault(key));
            Assert.False(quarantine.IsBanned(key));
            Assert.Equal(2, quarantine.RecordFault(key));
            Assert.True(quarantine.IsBanned(key));
        }
        finally
        {
            quarantine.Reset(key);
        }
    }

    private sealed class FakeNativePlugin : INativePlugin
    {
        private INativePluginHost? _host;
        private int _nextHandle;
        private int _lastHandle;

        public string Package => "test.native";

        /** 宿主在激活前写入的参数(JSON), 用于验证原生插件拿到的是自己那份 plugins.<包名> 段。 */
        public string ConfigJson { get; private set; } = "";

        public void Configure(string configJson) => ConfigJson = configJson;

        public bool Deactivated { get; private set; }

        public bool Faulting { get; init; }

        public bool ReturnsInvalidJson { get; init; }

        public NativePluginState State { get; private set; } = NativePluginState.Loaded;

        public void Activate(INativePluginHost host)
        {
            _host = host;
            State = NativePluginState.Active;
            host.Log(DshNativePluginAbi.LogInfo, "activated");
            _lastHandle = ++_nextHandle;
            host.RegisterTool(
                _lastHandle,
                "native_echo",
                "Echo a message",
                """{"type":"object","properties":{"message":{"type":"string"}}}""",
                input =>
                {
                    if (Faulting)
                        throw new NativePluginFaultException("boom");
                    if (ReturnsInvalidJson)
                        return "not json";
                    using var document = JsonDocument.Parse(input);
                    var message = document.RootElement.GetProperty("message").GetString();
                    return JsonSerializer.Serialize(new { text = $"echo:{message}" });
                });
        }

        public bool RemoveRegisteredTool() => _host?.UnregisterTool(_lastHandle) ?? false;

        public void RecordFault(string reason) => State = NativePluginState.Faulted;

        public void Deactivate() => Deactivated = true;

        public void Dispose()
        {
        }
    }
}
