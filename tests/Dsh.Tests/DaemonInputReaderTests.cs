using System.Net;
using System.Net.Sockets;
using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 输入旁路读取器: daemon 送来的就是终端原始字节, 所以粘贴标记、修饰键编码与中文歧义标点都走同一个解析器。
 * 这条用例钉住"新读取器真的按终端协议解码", 与 ChatPane 的插入行为(见 ChatWindowTranscriptTests)分开验证。
 */
public class DaemonInputReaderTests
{
    [Fact(Timeout = 30000)]
    public void ReadsPaste_ShiftEnter_AndCjkPunctuation_FromTheByteChannel()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            using var accepted = listener.AcceptTcpClient();
            using var reader = new DaemonInputReader(accepted.GetStream(), records: null);
            using var writer = client.GetStream();

            // ① 括号粘贴: 一个事件, 换行原样保留
            writer.Write("\u001b[200~第一行\r\n第二行\u001b[201~"u8);
            var paste = ReadUntil(reader, input => input.IsPaste);
            Assert.Equal("第一行\r\n第二行", paste.Paste);

            // ② 中文歧义标点: 逐字插入, 不被换成 ASCII
            writer.Write("“好”……——"u8);
            var text = new StringBuilder();
            while (text.Length < "“好”……——".Length)
                text.Append(ReadUntil(reader, _ => true).Key.KeyChar);
            Assert.Equal("“好”……——", text.ToString());

            // ③ Shift+Enter 的两种编码都解成"带 Shift 的 Enter"
            foreach (var sequence in new[] { "\u001b[13;2u", "\u001b[27;2;13~" })
            {
                writer.Write(Encoding.ASCII.GetBytes(sequence));
                var lineBreak = ReadUntil(reader, input => input.Key.Key == ConsoleKey.Enter);
                Assert.True((lineBreak.Key.Modifiers & ConsoleModifiers.Shift) != 0, sequence);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static TerminalInputEvent ReadUntil(DaemonInputReader reader, Func<TerminalInputEvent, bool> match)
    {
        var token = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var input = reader.Read(200);
            if (input is { } value && match(value))
                return value;
        }

        throw new InvalidOperationException("输入事件没有到达");
    }
}

