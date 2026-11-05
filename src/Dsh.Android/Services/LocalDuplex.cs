using System.IO.Pipelines;
using Dsh.Transport;

namespace Dsh.Android.Services;

/**
 * 手机内部的一对双向流: 本地 host 与本地客户端走它对话, 不经过任何网络。
 * 两个 Pipe 各负责一个方向, 复用的 StandardIoDuplexStream 把它们拼成普通 Stream。
 */
public static class LocalDuplex
{
    public static (Stream Server, Stream Client) Create()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = new StandardIoDuplexStream(
            toServer.Reader.AsStream(), toClient.Writer.AsStream(), ownsStreams: true);
        var client = new StandardIoDuplexStream(
            toClient.Reader.AsStream(), toServer.Writer.AsStream(), ownsStreams: true);
        return (server, client);
    }
}
