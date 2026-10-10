using System.Text;

namespace Dsh.PtyTerminal;

public static class TerminalPrompt
{
    public const string MarkerPrefix = "133;D;";
    public const string ControlledPrompt = "dsharp> ";
}

public sealed record SanitizedChunk(string Text, bool Prompt, string? PromptTail = null);

public sealed class TerminalSanitizer
{
    private string _pending = "";
    private string? _discardMode;
    private bool _discardOscEscape;
    private bool _trailingCarriageReturn;
    private bool _trackingPromptTail;
    private readonly int _maxPendingBytes;

    public TerminalSanitizer(int maxPendingBytes)
    {
        _maxPendingBytes = maxPendingBytes;
    }

    public SanitizedChunk Push(string chunk)
    {
        _pending += DiscardPrefix(chunk);
        var text = new StringBuilder();
        var prompt = false;
        var includePromptTail = _trackingPromptTail;
        var promptTail = new StringBuilder();
        var index = 0;
        void AppendText(string value)
        {
            text.Append(value);
            if (_trackingPromptTail)
                promptTail.Append(value);
        }
        while (index < _pending.Length)
        {
            var escape = _pending.IndexOf('\x1b', index);
            if (escape < 0)
            {
                AppendText(_pending[index..]);
                index = _pending.Length;
                break;
            }
            AppendText(_pending[index..escape]);
            if (escape + 1 >= _pending.Length)
            {
                index = escape;
                break;
            }
            var kind = _pending[escape + 1];
            if (kind == ']')
            {
                var bel = _pending.IndexOf('\x07', escape + 2);
                var stringTerminator = _pending.IndexOf("\u001b\\", escape + 2, StringComparison.Ordinal);
                var end = -1;
                if (bel >= 0 && stringTerminator >= 0)
                    end = Math.Min(bel + 1, stringTerminator + 2);
                else if (bel >= 0)
                    end = bel + 1;
                else if (stringTerminator >= 0)
                    end = stringTerminator + 2;
                if (end < 0)
                {
                    index = escape;
                    break;
                }
                var terminatorBytes = _pending[end - 1] == '\x07' ? 1 : 2;
                var content = _pending[(escape + 2)..(end - terminatorBytes)];
                if (content.StartsWith(TerminalPrompt.MarkerPrefix, StringComparison.Ordinal))
                {
                    prompt = true;
                    _trackingPromptTail = true;
                    includePromptTail = true;
                    promptTail.Clear();
                }
                index = end;
                continue;
            }
            if (kind == '[')
            {
                var end = escape + 2;
                while (end < _pending.Length)
                {
                    var code = _pending[end];
                    if (code >= '\x40' && code <= '\x7e')
                        break;
                    end += 1;
                }
                if (end >= _pending.Length)
                {
                    index = escape;
                    break;
                }
                index = end + 1;
                continue;
            }
            index = escape + 2;
        }
        _pending = _pending[index..];
        EnforcePendingBound();
        return new SanitizedChunk(
            NormalizeText(text.ToString()),
            prompt,
            includePromptTail ? promptTail.ToString() : null);
    }

    public string Flush()
    {
        var text = _pending.StartsWith('\x1b') ? "" : _pending;
        _pending = "";
        _discardMode = null;
        _discardOscEscape = false;
        _trackingPromptTail = false;
        var normalized = NormalizeText(text);
        if (!_trailingCarriageReturn)
            return normalized;
        _trailingCarriageReturn = false;
        return $"{normalized}\n";
    }

    private string NormalizeText(string text)
    {
        var complete = _trailingCarriageReturn ? $"\r{text}" : text;
        _trailingCarriageReturn = false;
        if (complete.EndsWith('\r'))
        {
            complete = complete[..^1];
            _trailingCarriageReturn = true;
        }
        return TerminalText.NormalizeTerminalText(complete);
    }

    private void EnforcePendingBound()
    {
        if (Encoding.UTF8.GetByteCount(_pending) <= _maxPendingBytes)
            return;
        _discardMode = _pending.Length > 1 && _pending[1] == ']' ? "osc" : "csi";
        _pending = "";
    }

    private string DiscardPrefix(string chunk)
    {
        if (_discardMode is null)
            return chunk;
        if (_discardMode == "csi")
        {
            for (var index = 0; index < chunk.Length; index += 1)
            {
                var code = chunk[index];
                if (code >= '\x40' && code <= '\x7e')
                {
                    _discardMode = null;
                    return chunk[(index + 1)..];
                }
            }
            return "";
        }

        var scan = 0;
        if (_discardOscEscape)
        {
            _discardOscEscape = false;
            if (chunk.StartsWith('\\'))
            {
                _discardMode = null;
                return chunk[1..];
            }
        }
        while (scan < chunk.Length)
        {
            if (chunk[scan] == '\x07')
            {
                _discardMode = null;
                return chunk[(scan + 1)..];
            }
            if (chunk[scan] == '\x1b')
            {
                if (scan + 1 < chunk.Length && chunk[scan + 1] == '\\')
                {
                    _discardMode = null;
                    return chunk[(scan + 2)..];
                }
                if (scan + 1 == chunk.Length)
                    _discardOscEscape = true;
            }
            scan += 1;
        }
        return "";
    }
}

public static class TerminalText
{
    /**
     * 单趟归一化: \r\n 与孤立 \r 折叠为 \n, 并剔除 BEL。
     * 无 \r/\x07 时原样返回, 避免热路径上的整串重建。
     */
    public static string NormalizeTerminalText(string text)
    {
        if (!RequiresNormalization(text))
            return text;
        return string.Create(NormalizedLength(text), text, static (destination, source) => NormalizeInto(source, destination));
    }

    private static bool RequiresNormalization(string text)
    {
        foreach (var c in text)
        {
            if (c is '\r' or '\x07')
                return true;
        }
        return false;
    }

    private static int NormalizedLength(string text)
    {
        var length = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\x07')
                continue;
            if (c == '\r')
            {
                length++;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                continue;
            }
            length++;
        }
        return length;
    }

    private static void NormalizeInto(string source, Span<char> destination)
    {
        var written = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var c = source[index];
            if (c == '\x07')
                continue;
            if (c == '\r')
            {
                destination[written++] = '\n';
                if (index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                continue;
            }
            destination[written++] = c;
        }
    }

    public static (string Text, bool Truncated) Utf8Tail(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return (text, false);
        var runes = text.EnumerateRunes().ToList();
        var bytes = 0;
        var start = runes.Count;
        for (var index = runes.Count - 1; index >= 0; index--)
        {
            var next = Encoding.UTF8.GetByteCount(runes[index].ToString());
            if (bytes + next > maxBytes)
                break;
            bytes += next;
            start = index;
        }
        return (string.Concat(runes.Skip(start).Select(rune => rune.ToString())), true);
    }

    public static string RetainTail(string text, int maxBytes)
        => Utf8Tail(text, maxBytes).Text;

    public static string RetainHead(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        var head = bytes.AsSpan(0, maxBytes).ToArray();
        var index = head.Length - 1;
        while (index >= 0 && (head[index] & 0xc0) == 0x80)
            index--;
        if (index < 0)
            return "";
        var lead = head[index];
        var expected = lead < 0x80 ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : lead < 0xf8 ? 4 : 0;
        if (expected == 0)
            return Encoding.UTF8.GetString(head);
        return head.Length - index < expected
            ? Encoding.UTF8.GetString(head[..index])
            : Encoding.UTF8.GetString(head);
    }
}