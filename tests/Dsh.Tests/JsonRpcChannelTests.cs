using PolyType;
using Dsh.Transport;
using Nerdbank.Streams;
using StreamJsonRpc;
using Dsh.RemoteHost.Rpc;

namespace Dsh.Tests;

/** JsonRpcChannel 的最小回环: 一条双工流、长度前缀帧、强类型代理, 一次往返。 */
public sealed partial class JsonRpcChannelTests
{
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IEcho
    {
        Task<string> EchoAsync(string text, CancellationToken cancellationToken = default);
    }

    private sealed class Echo : IEcho
    {
        public Task<string> EchoAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult($"echo:{text}");
    }

    [Fact]
    public async Task RoundTrip_OverLengthPrefixedFrames()
    {
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        using var server = JsonRpcChannel.Serve(serverSide, new SystemTextJsonFormatter(), new Echo());
        using var client = JsonRpcChannel.Connect(clientSide, new SystemTextJsonFormatter());
        var proxy = client.Attach<IEcho>();

        Assert.Equal("echo:hi", await proxy.EchoAsync("hi", TestContext.Current.CancellationToken));
    }

    /** 只实现一次的协议按方法名挂委托时也要能双向收发, 沙箱 worker 用的就是这条路径。 */
    [Fact]
    public async Task MethodRegistration_RoundTripsWithoutAnInterface()
    {
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        using var server = JsonRpcChannel.ServeMethods(serverSide, new SystemTextJsonFormatter(),
            ("sandbox/echo", (Func<string, string>)EchoText));
        using var client = JsonRpcChannel.Connect(clientSide, new SystemTextJsonFormatter());

        var echoed = await client.InvokeAsync<string>("sandbox/echo", TestContext.Current.CancellationToken, "hi");
        Assert.Equal("echo:hi", echoed);
    }

    /** stdio 包装流(读写分别是两条单向流)也要能过线, 沙箱 worker 就是这种管子。 */
    [Fact]
    public async Task RoundTrip_ThroughStandardIoDuplexStream()
    {
        var (clientToServerRead, clientToServerWrite) = FullDuplexStream.CreatePair();
        var (serverToClientRead, serverToClientWrite) = FullDuplexStream.CreatePair();
        await using var serverStream = new StandardIoDuplexStream(clientToServerRead, serverToClientWrite);
        await using var clientStream = new StandardIoDuplexStream(serverToClientRead, clientToServerWrite);
        using var server = JsonRpcChannel.Serve(serverStream, new SystemTextJsonFormatter(), new Echo());
        using var client = JsonRpcChannel.Connect(clientStream, new SystemTextJsonFormatter());

        Assert.Equal("echo:hi", await client.Attach<IEcho>().EchoAsync("hi", TestContext.Current.CancellationToken));
    }

    private static string EchoText(string text) => $"echo:{text}";

    [Fact]
    public async Task EveryHostContract_UsesAnExportedSourceGeneratedProxy()
    {
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        await using var unusedServer = serverSide;
        using var client = JsonRpcChannel.Connect(clientSide, HostRpcFormatter.CreateJson());

        AssertGenerated<IHostRpc>(client);
        AssertGenerated<IAgentRpc>(client);
        AssertGenerated<IFilesRpc>(client);
        AssertGenerated<IGitRpc>(client);
        AssertGenerated<INetworkRpc>(client);
        AssertGenerated<INotificationsRpc>(client);
        AssertGenerated<IPluginsRpc>(client);
        AssertGenerated<IPtyRpc>(client);
        AssertGenerated<ISessionEditingRpc>(client);
        AssertGenerated<IHostPush>(client);
    }

    private static void AssertGenerated<T>(JsonRpcChannel client) where T : class
        => Assert.Same(typeof(T).Assembly, client.Attach<T>().GetType().Assembly);

    [Fact]
    public void EveryHostContractPayload_HasGeneratedJsonMetadata()
    {
        var formatter = Assert.IsType<SystemTextJsonFormatter>(HostRpcFormatter.CreateJson());
        Type[] contracts = [typeof(IHostRpc), typeof(IAgentRpc), typeof(IFilesRpc), typeof(IGitRpc), typeof(INetworkRpc),
            typeof(INotificationsRpc), typeof(IPluginsRpc), typeof(IPtyRpc), typeof(ISessionEditingRpc), typeof(IHostPush)];
        foreach (var method in contracts.SelectMany(contract => contract.GetMethods()))
        {
            foreach (var parameter in method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)))
                Assert.NotNull(formatter.JsonSerializerOptions.GetTypeInfo(parameter.ParameterType));
            if (method.ReturnType.IsGenericType)
                Assert.NotNull(formatter.JsonSerializerOptions.GetTypeInfo(method.ReturnType.GenericTypeArguments.Single()));
        }
    }

    /** 沙箱那套 camelCase 上下文 + 自定义 Options 也要能过线。 */
    [Fact]
    public async Task SandboxFormatter_RoundTripsItsOwnRecords()
    {
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        using var server = JsonRpcChannel.ServeMethods(serverSide, Dsh.Plugins.Sandbox.PluginSandboxWire.Formatter(),
            ("sandbox/ready", (Func<Dsh.Plugins.Sandbox.PluginSandboxAck, Dsh.Plugins.Sandbox.PluginSandboxReady>)Ready));
        using var client = JsonRpcChannel.Connect(clientSide, Dsh.Plugins.Sandbox.PluginSandboxWire.Formatter());

        var ready = await client.InvokeAsync<Dsh.Plugins.Sandbox.PluginSandboxReady>(
            "sandbox/ready", TestContext.Current.CancellationToken, new Dsh.Plugins.Sandbox.PluginSandboxAck());
        Assert.Equal(Dsh.Plugins.Sandbox.PluginSandboxMethods.Version, ready.Version);
    }

    private static Dsh.Plugins.Sandbox.PluginSandboxReady Ready(Dsh.Plugins.Sandbox.PluginSandboxAck request)
        => new(Dsh.Plugins.Sandbox.PluginSandboxMethods.Version, 42);
}
