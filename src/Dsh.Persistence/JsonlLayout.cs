using System.Text;
using Dsh.Llm;

namespace Dsh.Persistence;

public enum JsonlCompression
{
    Zstd,
    None,
}

public static class JsonlLayout
{
    public const int MaxProjectSlugLength = 251;
    private const string ProjectKeyFallback = "root";

    public static string LogSuffix(JsonlCompression compression)
        => compression == JsonlCompression.Zstd ? ".jsonl.zstd" : ".jsonl";

    public static string EncodeSegment(string raw)
    {
        if (raw.Length == 0) throw new ArgumentException("cannot encode an empty path segment", nameof(raw));
        if (raw == ".") return "~002E";
        if (raw == "..") return "~002E~002E";
        var output = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch != '~' && IsSafeSegmentChar(ch)) output.Append(ch);
            else output.Append($"~{(int)ch:X4}");
        }
        return output.ToString();
    }

    public static string ProjectKey(string cwd)
    {
        if (cwd.Length == 0) throw new ArgumentException("cannot encode an empty project path", nameof(cwd));
        var readable = new StringBuilder(cwd.Length);
        var separatorRun = false;
        foreach (var ch in cwd)
        {
            if (ch is '/' or '\\' or ':')
            {
                if (!separatorRun) readable.Append('-');
                separatorRun = true;
            }
            else if (ch != '~' && IsSafeSegmentChar(ch))
            {
                readable.Append(ch);
                separatorRun = false;
            }
            else
            {
                readable.Append($"~{(int)ch:X4}");
                separatorRun = false;
            }
        }
        var slug = readable.ToString().TrimStart('-');
        if (slug.Length == 0) slug = ProjectKeyFallback;
        return $"--{slug[..Math.Min(MaxProjectSlugLength, slug.Length)]}--";
    }

    public static string ProjectDir(string root, string? cwd)
        => Path.Join(root, cwd is null ? "_no-cwd" : ProjectKey(cwd));

    public static string SessionDir(string root, string? cwd, SessionId id)
        => Path.Join(ProjectDir(root, cwd), EncodeSegment(id.Value));

    public static string LogPath(string root, string? cwd, SessionId id, JsonlCompression compression)
        => Path.Join(SessionDir(root, cwd, id), $"session{LogSuffix(compression)}");

    private static bool IsSafeSegmentChar(char ch)
        => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
}
