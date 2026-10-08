using System.Security.Cryptography;
using System.Text;

namespace Dsh.Core;

/** 项目级存储键:把项目路径编码成可读且文件系统安全的目录名,供会话日志、检查点等按项目分目录存储。 */
public static class ProjectStorageKey
{
    public const int MaxSlugLength = 251;

    /** 有界键的可读前缀长度: 与路径哈希一起把目录名总长限制在 ~56 字符内。 */
    public const int BoundedSlugLength = 32;

    private const int HashLength = 16;
    private const string Fallback = "root";

    public static string Of(string cwd)
    {
        var slug = Slug(cwd);
        return $"--{slug[..Math.Min(MaxSlugLength, slug.Length)]}--";
    }

    /**
     * 有界长度的存储键: 可读前缀 + 路径哈希。
     * 供需要深层子目录的存储(如 checkpoints 的 git 仓库对象目录)使用,
     * 避免"完整路径编码 + 深层子目录"叠加后超过 Windows MAX_PATH。
     */
    public static string BoundedOf(string cwd)
    {
        var slug = Slug(cwd);
        var prefix = slug.Length <= BoundedSlugLength ? slug : slug[..BoundedSlugLength];
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)));
        return $"--{prefix}-{hash[..HashLength]}--";
    }

    public static bool IsSafeSegmentChar(char ch)
        => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';

    /** 路径规整成可读 slug(本身可能很长, 由调用方决定是否截断)。 */
    private static string Slug(string cwd)
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
        return slug.Length == 0 ? Fallback : slug;
    }
}
