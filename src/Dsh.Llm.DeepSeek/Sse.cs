using System.Runtime.CompilerServices;
using System.Text;

namespace Dsh.Llm.DeepSeek;

public static class SseParser
{
    public const string Done = "[DONE]";

    public static async IAsyncEnumerable<string> Parse(
        Stream stream,
        Action<string>? onComment = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decoder = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var dataLines = new StringBuilder();
        while (true)
        {
            var line = await decoder.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new LlmException(new LlmFailure("SSE stream ended without [DONE]", LlmFailureCodes.StreamClosed));
            }
            if (line.Length == 0)
            {
                if (dataLines.Length > 0)
                {
                    var data = dataLines.ToString();
                    dataLines.Clear();
                    yield return data;
                    if (data == Done)
                        yield break;
                }
                continue;
            }
            if (line[0] == ':')
            {
                onComment?.Invoke(line[1..].TrimStart());
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line["data:".Length..];
                if (value.StartsWith(' '))
                    value = value[1..];
                if (dataLines.Length > 0)
                    dataLines.Append('\n');
                dataLines.Append(value);
            }
        }
    }
}
