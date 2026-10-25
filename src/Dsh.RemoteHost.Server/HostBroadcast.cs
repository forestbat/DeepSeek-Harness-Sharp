using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Dsh.RemoteHost;

/** 一条服务端推送: 只序列化一次, 再分发给每个订阅它的连接。 */
public readonly record struct HostNotification(string Method, JsonElement Params)
{
    public static HostNotification Create<T>(string method, T value, JsonTypeInfo<T> info)
        => new(method, JsonSerializer.SerializeToElement(value, info));
}

/**
 * 推送扇出: 每个连接一条独立队列, 慢连接或断开的连接不会挡住别的连接。
 * 所有者在持有自己的锁时调用 Publish 与 Subscribe, 使"先补发当前状态、再接实时推送"对每个订阅者都是原子的。
 */
public sealed class HostBroadcast
{
    private readonly Lock _gate = new();
    private readonly List<Channel<HostNotification>> _subscribers = [];

    /** 当前订阅数: 每个存活的 host 连接各持有一份订阅。 */
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
                return _subscribers.Count;
        }
    }

    public void Publish(HostNotification notification)
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(notification);
        }
    }

    /** 新订阅先收到 replay 里的推送, 之后是 Publish 的实时推送。 */
    public HostSubscription Subscribe(IEnumerable<HostNotification> replay)
    {
        var channel = Channel.CreateUnbounded<HostNotification>(new UnboundedChannelOptions { SingleReader = true });
        foreach (var notification in replay)
            channel.Writer.TryWrite(notification);
        lock (_gate)
            _subscribers.Add(channel);
        return new HostSubscription(channel.Reader, () => Unsubscribe(channel));
    }

    private void Unsubscribe(Channel<HostNotification> channel)
    {
        lock (_gate)
            _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }
}

/** 一个连接对某个推送源的订阅; 释放即退订。 */
public sealed class HostSubscription(ChannelReader<HostNotification> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<HostNotification> Reader => reader;

    public void Dispose() => unsubscribe();
}
