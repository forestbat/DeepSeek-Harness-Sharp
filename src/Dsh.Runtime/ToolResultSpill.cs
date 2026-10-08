using System.Security.Cryptography;
using System.Text;

namespace Dsh.Runtime;

public sealed record ToolResultSpillOptions(
    long MaxBytes = 64 * 1024,
    int HeadChars = 4096,
    int TailChars = 1024);

/**
 * 大工具结果落盘: 正文超过阈值时写入持久化根下的 tool-results 目录, 上下文里只保留头尾与文件路径,
 * 避免单个工具输出(整份文件/长日志)撑爆上下文窗口。落盘失败时原样返回, 不影响主流程。
 */
public sealed class ToolResultSpill : Service
{
    public const string ServiceName = "toolResultSpill";

    private readonly string? _root;

    public ToolResultSpillOptions Options { get; }

    public ToolResultSpill(Context ctx, ToolResultSpillOptions? options = null) : base(ctx, ServiceName)
    {
        Options = options ?? new ToolResultSpillOptions();
        _root = ctx.GetProp("dshHomePath") is string { Length: > 0 } home
            ? Path.Combine(home, "tool-results")
            : null;
    }

    public bool ShouldSpill(string text) => _root is not null && text.Length > Options.MaxBytes;

    public string Spill(string text, string callId)
    {
        if (_root is null || text.Length <= Options.MaxBytes)
            return text;
        try
        {
            Directory.CreateDirectory(_root);
            var name = $"{Sanitize(callId)}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}.txt";
            var path = Path.Combine(_root, name);
            if (!File.Exists(path))
                File.WriteAllText(path, text);
            var head = Slice(text, 0, Options.HeadChars);
            var tail = Slice(text, text.Length - Options.TailChars, Options.TailChars);
            return $"{head}\n\n[... {text.Length} chars spilled to {path} ...]\n\n{tail}";
        }
        catch (IOException)
        {
            return text;
        }
        catch (UnauthorizedAccessException)
        {
            return text;
        }
    }

    private static string Slice(string text, int start, int length)
    {
        var begin = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(begin + length, begin, text.Length);
        if (begin > 0 && char.IsLowSurrogate(text[begin]))
            begin--;
        if (end < text.Length && end > 0 && char.IsLowSurrogate(text[end]))
            end--;
        return text[begin..end];
    }

    private static string Sanitize(string callId)
    {
        var buffer = new StringBuilder(callId.Length);
        foreach (var character in callId)
            buffer.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        return buffer.Length == 0 ? "result" : buffer.ToString();
    }
}
