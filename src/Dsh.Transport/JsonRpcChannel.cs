using System.Runtime.CompilerServices;
using StreamJsonRpc;

namespace Dsh.Transport;

/** A statically described contract and its implementation on one RPC connection. */
public readonly record struct JsonRpcTarget(RpcTargetMetadata Metadata, object Instance);

/**
 * 跨进程 RPC 的唯一入口。
 *
 * 给定一条 Stream 和契约接口类型, 返回已 Attach 的强类型代理; 服务端把实现契约的对象挂在同一条 Stream 上。
 * 帧格式固定为长度前缀, 序列化由调用方传入的 formatter 决定: 生产用源生成 JSON, 抓包排障时换别的 formatter。
 * 关闭与断线统一由 JsonRpc.Completion 表达, 调用方不再自己维护运行状态。
 */
public sealed class JsonRpcChannel : IDisposable
{
    /** 单帧上限: 供需要自己判断载荷大小的调用方(浏览器 WebSocket)使用。 */
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /** 长度前缀占 4 字节(大端)。 */
    public const int HeaderLength = 4;

    /** JSON-RPC 标准错误码: 方法不存在。 */
    public const int MethodNotFound = -32601;

    private readonly JsonRpc _rpc;

    private JsonRpcChannel(JsonRpc rpc) => _rpc = rpc;

    /** 连接结束(正常关闭或对端断开)时完成。 */
    public Task Completion => _rpc.Completion;

    /** Registers statically described local contracts for reverse calls on an outgoing connection. */
    public static JsonRpcChannel Connect(Stream duplex, IJsonRpcMessageFormatter formatter, params JsonRpcTarget[] contracts)
        => Build(new LengthHeaderMessageHandler(duplex, duplex, formatter), contracts, []);

    /** Registers statically described local contracts on an incoming connection. */
    public static JsonRpcChannel Serve(Stream duplex, IJsonRpcMessageFormatter formatter, params JsonRpcTarget[] contracts)
        => Build(new LengthHeaderMessageHandler(duplex, duplex, formatter), contracts, []);

    public static JsonRpcChannel Connect(Stream duplex, IJsonRpcMessageFormatter formatter, IReadOnlyList<JsonRpcTarget> contracts,
        (string Method, Delegate Handler)[] methods)
        => Build(new LengthHeaderMessageHandler(duplex, duplex, formatter), contracts, methods);

    public static JsonRpcChannel Serve(Stream duplex, IJsonRpcMessageFormatter formatter, IReadOnlyList<JsonRpcTarget> contracts,
        (string Method, Delegate Handler)[] methods)
        => Build(new LengthHeaderMessageHandler(duplex, duplex, formatter), contracts, methods);

    /** 只实现一次的内部协议: 直接按方法名挂委托, 不必为此定义接口。 */
    public static JsonRpcChannel ServeMethods(Stream duplex, IJsonRpcMessageFormatter formatter,
        params (string Method, Delegate Handler)[] methods)
        => Build(new LengthHeaderMessageHandler(duplex, duplex, formatter), contracts: [], methods: methods);

    private static JsonRpcChannel Build(IJsonRpcMessageHandler handler, IReadOnlyList<JsonRpcTarget> contracts,
        (string Method, Delegate Handler)[] methods)
    {
        var rpc = new JsonRpc(handler);
        foreach (var contract in contracts)
            rpc.AddLocalRpcTarget(contract.Metadata, contract.Instance, null);
        foreach (var (method, implementation) in methods)
            rpc.AddLocalRpcMethod(method, implementation);
        rpc.StartListening();
        return new JsonRpcChannel(rpc);
    }

    /**
     * 取契约接口的代理, 指向对端注册的那份实现。
     *
     * StreamJsonRpc 的强类型代理是运行时生成的(Reflection.Emit), NativeAOT 下必然不可用。
     * 那种情况下库只会抛一句 "CreateProxy is not supported if dynamic code is not supported",
     * 从这里就能看出是"这个入口被编成了 AOT", 而不是协议或配置出错。
     */
    public T Attach<T>() where T : class
        => RuntimeFeature.IsDynamicCodeSupported
            ? _rpc.Attach<T>()
            : throw new NotSupportedException(
                $"transferring {typeof(T).Name} as a typed JSON-RPC proxy needs runtime code generation; "
                + "this entry must be published as the JIT build, not NativeAOT");

    /** 按方法名调用(不带接口)。参数按位置序列化。 */
    public async Task<T> InvokeAsync<T>(string method, CancellationToken cancellationToken, params object?[] args)
        => await _rpc.InvokeAsync<T>(method, args).WaitAsync(cancellationToken).ConfigureAwait(false);

    /**
     * 按方法名调用并拿回原始 JSON。
     * 只给"对端协议本身就是字符串方法名"的适配层用(浏览器中继), 业务代码一律走 Attach 出来的强类型代理。
     * 参数按"单个位置参数"发送: host 侧契约方法都只收一个 DTO, 按命名参数发会因属性名对不上而找不到方法。
     * 这条路径不支持调用方取消(StreamJsonRpc 的位置参数重载没有 CancellationToken); 中继靠关闭连接来中止。
     */
    public async Task<System.Text.Json.JsonElement> InvokeAsync(string method, System.Text.Json.JsonElement? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return parameters is { } payload
            ? await _rpc.InvokeAsync<System.Text.Json.JsonElement>(method, new object?[] { payload }).ConfigureAwait(false)
            : await _rpc.InvokeAsync<System.Text.Json.JsonElement>(method, []).ConfigureAwait(false);
    }

    public void Dispose() => _rpc.Dispose();
}
